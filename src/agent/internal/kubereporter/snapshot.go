// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubelocal"
	pb "github.com/framlux/vord/internal/proto/agent"
)

const (
	// snapshotInterval is how often the holder reads the cluster.
	snapshotInterval = 15 * time.Minute

	// snapshotHeartbeat is how often an unchanged snapshot is sent anyway, so the server keeps hearing from a
	// cluster that does not change. It is well inside the 90 minutes after which the server treats a cluster's
	// reporter as gone.
	snapshotHeartbeat = 30 * time.Minute

	// snapshotResendAfter is how long an unchanged snapshot is held back. Collections come every snapshotInterval
	// give or take a little, so a threshold of exactly snapshotHeartbeat would let a tick that comes a few seconds
	// early wait a whole interval more. Half an interval short of it, the resend goes out on the first tick near
	// the heartbeat, and the tick before that, a full interval earlier, is still too soon.
	snapshotResendAfter = snapshotHeartbeat - snapshotInterval/2

	// snapshotMaxBytes caps the encoded size of one snapshot. The nodes beyond it are left out.
	snapshotMaxBytes = 256 * 1024

	// snapshotPageSize is the page size of the node and pod lists. A page is decoded whole, so it bounds the memory
	// a list needs.
	snapshotPageSize = 200

	// snapshotCollectTimeout bounds one collection, however many pages the cluster serves, so a collection can
	// never run on and on while the lists are being fed to it.
	snapshotCollectTimeout = 5 * time.Minute
)

// The paths a collection reads.
const (
	kubeSystemPath = "/api/v1/namespaces/kube-system"
	versionPath    = "/version"
	nodesPath      = "/api/v1/nodes"
	podsPath       = "/api/v1/pods"

	// livePodSelector keeps the pods that still run or may yet run, and leaves out the finished ones.
	livePodSelector = "status.phase!=Succeeded,status.phase!=Failed"

	// nodeRolePrefix starts the label key that marks a node's role; the rest of the key is the role.
	nodeRolePrefix = "node-role.kubernetes.io/"
)

// reportedConditions are the node conditions a snapshot carries, and only while their status is not False.
var reportedConditions = map[string]bool{
	"MemoryPressure":     true,
	"DiskPressure":       true,
	"PIDPressure":        true,
	"NetworkUnavailable": true,
}

// SnapshotQueue is where a finished cluster snapshot is queued for delivery. Queueing replaces every snapshot
// that has not been delivered yet.
type SnapshotQueue interface {
	ReplaceTelemetryOfType(id string, t db.TelemetryType, payload string) error
}

// leadership is what a collection needs to know of the election: whether this agent leads, and the tenure and
// Lease epoch it leads in. All three come from one reading, so a snapshot never carries a count from one tenure
// and an epoch from another.
type leadership interface {
	Leadership() (transitions int64, leaseUID string, leaseCreatedAt string, ok bool)
}

// snapshotter collects the cluster snapshots a Lease holder sends. It remembers what it last sent and how many
// error snapshots it made in a row. The holder's tenure bounds both: when a tenure ends, so does what the
// snapshotter remembers of it.
type snapshotter struct {
	queue  SnapshotQueue
	facts  func(ctx context.Context) (kubelocal.Facts, bool)
	clock  Clock
	paused func() bool
	newID  func() string

	// lostGen counts the tenures that have ended. A collection reads it before it confirms its tenure and checks it
	// again once it holds mu, so a tenure that ends in between is noticed and its snapshot is not queued after the
	// queue was dropped. A loss is not always a change of holder: an agent that lost the Lease by missing its renew
	// deadline can take its own claim back in place, and a collection that confirmed its tenure just before the
	// loss may still queue its snapshot after that re-take. That snapshot is harmless: the acquisition starts a
	// collection of its own, which replaces it.
	lostGen atomic.Uint64

	// errorRun is how many error snapshots in a row the holder has made. The health gate reads it from the
	// election goroutine, so it is not under mu.
	errorRun atomic.Int32

	// mu serialises queueing a snapshot with the end of a tenure, and guards the fields below it.
	mu         sync.Mutex
	sent       bool
	lastHash   [sha256.Size]byte
	lastSentAt time.Time
}

func newSnapshotter(queue SnapshotQueue, facts func(ctx context.Context) (kubelocal.Facts, bool), clock Clock, paused func() bool) *snapshotter {
	return &snapshotter{queue: queue, facts: facts, clock: clock, paused: paused, newID: id.NewV7}
}

// consecutiveErrors is how many error snapshots in a row the holder has made; the health gate reads it.
func (s *snapshotter) consecutiveErrors() int {
	return int(s.errorRun.Load())
}

// forgetLastSent makes the next snapshot go out whatever it holds. It is what an acquisition does, because the
// snapshots queued by an earlier tenure were dropped when it ended.
func (s *snapshotter) forgetLastSent() {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.sent = false
}

// tenureEnded is called when this agent stops being the holder. It forgets what the tenure sent and counted, and
// runs drop, which removes the snapshots the tenure queued. It waits for a snapshot that is being queued, so the
// drop is never overtaken by an insert from the tenure it ends.
func (s *snapshotter) tenureEnded(drop func()) {
	s.lostGen.Add(1)

	s.mu.Lock()
	defer s.mu.Unlock()

	s.errorRun.Store(0)
	s.sent = false

	if drop != nil {
		drop()
	}
}

// collect reads the cluster once and queues the snapshot if it changed or is due. It does nothing unless this
// agent leads and the host is not paused. newClient builds the client the cluster is read with.
//
// Leadership is read once, before the cluster is read, and the snapshot is stamped with that reading. It is read
// again before the snapshot is queued, and a snapshot whose tenure ended in between is dropped.
func (s *snapshotter) collect(ctx context.Context, lead leadership, newClient func() (*kube.Client, error)) {
	if (s.paused != nil) && s.paused() {
		slog.Debug("host is paused, not collecting a cluster snapshot")

		return
	}

	transitions, leaseUID, leaseCreatedAt, leading := lead.Leadership()
	if leading == false {
		return
	}

	startedAt := s.clock.Now()

	collectCtx, cancel := context.WithTimeout(ctx, snapshotCollectTimeout)
	defer cancel()

	record, err := s.gather(collectCtx, newClient)

	// A collection cut short by the agent stopping says nothing about the cluster.
	if ctx.Err() != nil {
		return
	}

	failed := err != nil
	if failed {
		reason := snapshotErrorReason(err)
		slog.Warn("could not read the cluster for a snapshot", "reason", reason, "error", err)

		record = &pb.KubernetesClusterRecord{ReporterError: reason}
	}

	record.CaFingerprint = s.caFingerprint(collectCtx)
	record.LeaseTransitions = transitions
	record.LeaseUid = leaseUID
	record.LeaseCreatedAt = leaseCreatedAt

	fitRecord(record, snapshotMaxBytes)

	s.queueIfDue(lead, record, failed, startedAt)
}

// queueIfDue queues the snapshot when its tenure still holds and it changed or is due. startedAt, when the
// collection began, is the moment the snapshot is dated by.
func (s *snapshotter) queueIfDue(lead leadership, record *pb.KubernetesClusterRecord, failed bool, startedAt time.Time) {
	generation := s.lostGen.Load()

	transitions, leaseUID, leaseCreatedAt, leading := lead.Leadership()
	if (leading == false) || (transitions != record.LeaseTransitions) || (leaseUID != record.LeaseUid) || (leaseCreatedAt != record.LeaseCreatedAt) {
		slog.Debug("not queueing the cluster snapshot, the lease tenure it was taken in has ended")

		return
	}

	if (s.paused != nil) && s.paused() {
		slog.Debug("host was paused while the cluster was read, not queueing the snapshot")

		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.lostGen.Load() != generation {
		slog.Debug("not queueing the cluster snapshot, the lease tenure ended")

		return
	}

	if failed {
		s.errorRun.Add(1)
	} else {
		s.errorRun.Store(0)
	}

	hash, err := hashSnapshot(record)
	if err != nil {
		slog.Warn("could not encode the cluster snapshot", "error", err)

		return
	}

	if s.sent && (hash == s.lastHash) && (startedAt.Sub(s.lastSentAt) < snapshotResendAfter) {
		slog.Debug("the cluster snapshot has not changed")

		return
	}

	payload, err := protojson.MarshalOptions{UseProtoNames: true}.Marshal(record)
	if err != nil {
		slog.Warn("could not encode the cluster snapshot", "error", err)

		return
	}

	if err := s.queue.ReplaceTelemetryOfType(s.newID(), db.TelemetryKubernetesCluster, string(payload)); err != nil {
		slog.Warn("could not queue the cluster snapshot", "error", err)

		return
	}

	s.sent = true
	s.lastHash = hash
	s.lastSentAt = startedAt

	slog.Info("queued a cluster snapshot", "nodes", len(record.Nodes), "truncated", record.Truncated, "reporter_error", record.ReporterError)
}

// caFingerprint is this host's fingerprint of the cluster CA, from local detection, or empty when it cannot be
// read.
func (s *snapshotter) caFingerprint(ctx context.Context) string {
	if s.facts == nil {
		return ""
	}

	facts, _ := s.facts(ctx)

	return facts.CaFingerprint
}

// snapshotErrorReason names why a collection failed: the cluster refused the credentials, the certificate it
// presented is not the one the stored CA vouches for, or it could not be read for some other reason.
func snapshotErrorReason(err error) string {
	switch {
	case errors.Is(err, errNamespaceTerminating):
		return reasonNamespaceTerminating
	case kube.IsAuthRejected(err):
		return reasonCredentialsRejected
	case kube.IsTLSVerify(err):
		return reasonTLSCAChanged
	}

	return reasonAPIUnreachable
}

// gather reads the cluster. The record it returns holds everything but what only the holder's tenure knows: the
// CA fingerprint and the Lease epoch.
func (s *snapshotter) gather(ctx context.Context, newClient func() (*kube.Client, error)) (*pb.KubernetesClusterRecord, error) {
	client, err := newClient()
	if err != nil {
		return nil, fmt.Errorf("build the client: %w", err)
	}

	kubeSystem, err := client.GetRaw(ctx, kubeSystemPath)
	if err != nil {
		return nil, fmt.Errorf("read the kube-system namespace: %w", err)
	}

	version, err := client.GetRaw(ctx, versionPath)
	if err != nil {
		return nil, fmt.Errorf("read the cluster version: %w", err)
	}

	lease, err := client.GetRaw(ctx, leasePath)
	if err != nil {
		return nil, fmt.Errorf("read the reporter lease: %w", err)
	}

	namespace, err := client.GetRaw(ctx, namespacePath)
	if err != nil {
		return nil, fmt.Errorf("read the reporter namespace: %w", err)
	}

	if stringAt(namespace, "metadata", "deletionTimestamp") != "" {
		return nil, errNamespaceTerminating
	}

	nodes, err := listNodes(ctx, client)
	if err != nil {
		return nil, fmt.Errorf("list the nodes: %w", err)
	}

	podCounts, err := countPodsByNode(ctx, client)
	if err != nil {
		return nil, fmt.Errorf("list the pods: %w", err)
	}

	for _, node := range nodes {
		node.PodCount = podCounts[node.Name]
	}

	// A Lease with no role version, or one that is not a number, is at version 0, which is outdated.
	roleVersion := roleVersionOf(lease)

	return &pb.KubernetesClusterRecord{
		KubeSystemUid:  stringAt(kubeSystem, "metadata", "uid"),
		ClusterVersion: stringAt(version, "gitVersion"),
		RoleVersion:    int32(min(roleVersion, math.MaxInt32)),
		RoleOutdated:   roleVersion < CompiledRoleVersion,
		Nodes:          nodes,
	}, nil
}

// nodePage is the part of a node list page a snapshot keeps.
type nodePage struct {
	Items []struct {
		Metadata struct {
			Name   string            `json:"name"`
			Labels map[string]string `json:"labels"`
		} `json:"metadata"`
		Spec struct {
			Unschedulable bool `json:"unschedulable"`
		} `json:"spec"`
		Status struct {
			Conditions []struct {
				Type               string `json:"type"`
				Status             string `json:"status"`
				LastTransitionTime string `json:"lastTransitionTime"`
			} `json:"conditions"`
			NodeInfo struct {
				MachineID               string `json:"machineID"`
				KubeletVersion          string `json:"kubeletVersion"`
				ContainerRuntimeVersion string `json:"containerRuntimeVersion"`
			} `json:"nodeInfo"`
			Allocatable struct {
				Pods any `json:"pods"`
			} `json:"allocatable"`
		} `json:"status"`
	} `json:"items"`
}

// listNodes reads every node, a page at a time, and returns them sorted by name.
func listNodes(ctx context.Context, client *kube.Client) ([]*pb.KubernetesClusterNode, error) {
	byName := map[string]*pb.KubernetesClusterNode{}

	err := client.List(ctx, nodesPath, nil, snapshotPageSize, func(body []byte) error {
		var page nodePage
		if err := json.Unmarshal(body, &page); err != nil {
			return fmt.Errorf("decode a page of nodes: %w", err)
		}

		for _, item := range page.Items {
			if item.Metadata.Name == "" {
				continue
			}

			node := &pb.KubernetesClusterNode{
				Name:                    item.Metadata.Name,
				Roles:                   rolesOf(item.Metadata.Labels),
				ReadyStatus:             "Unknown",
				Unschedulable:           item.Spec.Unschedulable,
				MachineId:               item.Status.NodeInfo.MachineID,
				KubeletVersion:          item.Status.NodeInfo.KubeletVersion,
				ContainerRuntimeVersion: item.Status.NodeInfo.ContainerRuntimeVersion,
				PodCapacity:             podCapacityOf(item.Status.Allocatable.Pods),
			}

			for _, condition := range item.Status.Conditions {
				switch {
				case condition.Type == "Ready":
					node.ReadyStatus = condition.Status
					node.ReadySince = condition.LastTransitionTime
				case reportedConditions[condition.Type] && (condition.Status != "False"):
					node.Conditions = append(node.Conditions, &pb.KubernetesNodeCondition{
						Type:               condition.Type,
						Status:             condition.Status,
						LastTransitionTime: condition.LastTransitionTime,
					})
				}
			}

			sort.Slice(node.Conditions, func(i, j int) bool { return node.Conditions[i].Type < node.Conditions[j].Type })

			byName[node.Name] = node
		}

		return nil
	}, func() {
		// The cluster expired the list and it starts over; what the abandoned pass gathered is out of date.
		clear(byName)
	})
	if err != nil {
		return nil, err
	}

	nodes := make([]*pb.KubernetesClusterNode, 0, len(byName))
	for _, node := range byName {
		nodes = append(nodes, node)
	}

	sort.Slice(nodes, func(i, j int) bool { return nodes[i].Name < nodes[j].Name })

	return nodes, nil
}

// rolesOf returns the roles a node's labels name, sorted: the part of each node-role label's key after the
// prefix. The label's value is not looked at.
func rolesOf(labels map[string]string) []string {
	var roles []string

	for key := range labels {
		if role, found := strings.CutPrefix(key, nodeRolePrefix); found && (role != "") {
			roles = append(roles, role)
		}
	}

	sort.Strings(roles)

	return roles
}

// podCapacityOf reads allocatable.pods, which the API gives as a quantity string, or 0 when it is missing or not
// a count.
func podCapacityOf(value any) int32 {
	var capacity int64

	switch typed := value.(type) {
	case string:
		parsed, err := strconv.ParseInt(typed, 10, 32)
		if err != nil {
			return 0
		}

		capacity = parsed
	case float64:
		capacity = int64(typed)
	default:
		return 0
	}

	if (capacity < 0) || (capacity > math.MaxInt32) {
		return 0
	}

	return int32(capacity)
}

// podPage is the part of a pod list page a snapshot keeps: where each pod runs.
type podPage struct {
	Items []struct {
		Spec struct {
			NodeName string `json:"nodeName"`
		} `json:"spec"`
	} `json:"items"`
}

// countPodsByNode counts the pods that have not finished on each node, reading a page at a time. A pod that is
// not on a node yet is counted nowhere.
func countPodsByNode(ctx context.Context, client *kube.Client) (map[string]int32, error) {
	counts := map[string]int32{}

	query := url.Values{"fieldSelector": {livePodSelector}}

	err := client.List(ctx, podsPath, query, snapshotPageSize, func(body []byte) error {
		var page podPage
		if err := json.Unmarshal(body, &page); err != nil {
			return fmt.Errorf("decode a page of pods: %w", err)
		}

		for _, item := range page.Items {
			if item.Spec.NodeName != "" {
				counts[item.Spec.NodeName]++
			}
		}

		return nil
	}, func() {
		// The cluster expired the list and it starts over; counting on top of the abandoned pass would count
		// every pod it saw twice.
		clear(counts)
	})
	if err != nil {
		return nil, err
	}

	return counts, nil
}

// fitRecord leaves the record within maxBytes of encoded proto, and within the queue's own payload limit when
// written as JSON. Nodes were sorted by name when they were read; those that do not fit are dropped from the end,
// the largest prefix that fits is kept, and the record says it was truncated. A record that already fits is left
// whole.
func fitRecord(record *pb.KubernetesClusterRecord, maxBytes int) {
	all := record.Nodes

	fits := func(keep int) bool {
		record.Nodes = all[:keep]

		if proto.Size(record) > maxBytes {
			return false
		}

		payload, err := protojson.MarshalOptions{UseProtoNames: true}.Marshal(record)

		return (err == nil) && (len(payload) <= db.MaxPayloadBytes)
	}

	if fits(len(all)) {
		return
	}

	record.Truncated = true

	// The size only grows with the number of nodes kept, so the largest prefix that fits is found by halving.
	low, high := 0, len(all)-1
	for low < high {
		middle := (low + high + 1) / 2
		if fits(middle) {
			low = middle
		} else {
			high = middle - 1
		}
	}

	record.Nodes = all[:low]
}

// hashSnapshot is the SHA-256 of the snapshot as encoded in a fixed order, leaving out the pod counts: those move
// all the time and are not a reason to send. The Lease epoch and the transition count are part of it, so the
// first snapshot of a tenure is never mistaken for the last of the one before. The only timestamps in a snapshot
// are the transition times of conditions, which are part of a node's state.
func hashSnapshot(record *pb.KubernetesClusterRecord) ([sha256.Size]byte, error) {
	view, ok := proto.Clone(record).(*pb.KubernetesClusterRecord)
	if ok == false {
		return [sha256.Size]byte{}, errors.New("the snapshot could not be copied")
	}

	for _, node := range view.Nodes {
		node.PodCount = 0
	}

	encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(view)
	if err != nil {
		return [sha256.Size]byte{}, err
	}

	return sha256.Sum256(encoded), nil
}
