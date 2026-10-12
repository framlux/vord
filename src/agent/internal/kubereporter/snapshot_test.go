// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubelocal"
	pb "github.com/framlux/vord/internal/proto/agent"
)

const (
	testKubeSystemUID  = "kube-system-uid-1"
	testClusterVersion = "v1.31.4+k3s1"
	testFingerprint    = "fp-ca"
	testLeaseUID       = "lease-uid-1"
	testLeaseCreated   = "2026-10-11T08:00:00Z"
	testTransitions    = int64(7)
)

// queuedSnapshot is one snapshot the fake queue was given.
type queuedSnapshot struct {
	id       string
	itemType db.TelemetryType
	payload  string
}

// fakeSnapshotQueue records the snapshots handed to it, and can be told to fail.
type fakeSnapshotQueue struct {
	mu       sync.Mutex
	items    []queuedSnapshot
	err      error
	replaced chan struct{}
}

func newFakeSnapshotQueue() *fakeSnapshotQueue {
	return &fakeSnapshotQueue{replaced: make(chan struct{}, 1000)}
}

func (q *fakeSnapshotQueue) ReplaceTelemetryOfType(id string, t db.TelemetryType, payload string) error {
	q.mu.Lock()
	defer q.mu.Unlock()

	if q.err != nil {
		return q.err
	}

	q.items = append(q.items, queuedSnapshot{id: id, itemType: t, payload: payload})
	q.replaced <- struct{}{}

	return nil
}

// count is how many snapshots were queued.
func (q *fakeSnapshotQueue) count() int {
	q.mu.Lock()
	defer q.mu.Unlock()

	return len(q.items)
}

// record decodes the n-th queued snapshot the way the sender does, failing the test when it cannot be decoded.
func (q *fakeSnapshotQueue) record(t *testing.T, n int) *pb.KubernetesClusterRecord {
	t.Helper()

	q.mu.Lock()
	defer q.mu.Unlock()

	if n >= len(q.items) {
		t.Fatalf("snapshot %d requested but only %d were queued", n, len(q.items))
	}

	var record pb.KubernetesClusterRecord
	if err := protojson.Unmarshal([]byte(q.items[n].payload), &record); err != nil {
		t.Fatalf("snapshot %d is not a KubernetesClusterRecord the sender can read: %v", n, err)
	}

	return &record
}

// last decodes the most recent queued snapshot.
func (q *fakeSnapshotQueue) last(t *testing.T) *pb.KubernetesClusterRecord {
	t.Helper()

	return q.record(t, q.count()-1)
}

// fakeLeadership answers the leadership questions from fields the test sets. onCall runs before each answer with
// the number of the call, which lets a test change the answer mid-collection.
type fakeLeadership struct {
	transitions int64
	uid         string
	created     string
	ok          bool
	calls       int
	onCall      func(call int)
}

func (l *fakeLeadership) Leadership() (int64, string, string, bool) {
	l.calls++

	if l.onCall != nil {
		l.onCall(l.calls)
	}

	if l.ok == false {
		return 0, "", "", false
	}

	return l.transitions, l.uid, l.created, true
}

// snapshotRig is one holder's snapshot collector on a fake cluster with a healthy two-node cluster in it.
type snapshotRig struct {
	t      *testing.T
	api    *fakeAPI
	clock  *fakeClock
	queue  *fakeSnapshotQueue
	lead   *fakeLeadership
	snaps  *snapshotter
	token  string
	caPEM  []byte
	paused bool
	facts  kubelocal.Facts
}

func newSnapshotRig(t *testing.T) *snapshotRig {
	t.Helper()

	api := newFakeAPI(t)
	api.seed(leasePath, leaseManifest(true))
	seedHealthyCluster(api)

	rig := &snapshotRig{
		t:     t,
		api:   api,
		clock: newFakeClock(),
		queue: newFakeSnapshotQueue(),
		lead:  &fakeLeadership{transitions: testTransitions, uid: testLeaseUID, created: testLeaseCreated, ok: true},
		token: api.issueReporterToken(),
		caPEM: api.caPEM,
		facts: kubelocal.Facts{CaFingerprint: testFingerprint},
	}

	rig.snaps = newSnapshotter(rig.queue, func(context.Context) (kubelocal.Facts, bool) { return rig.facts, true }, rig.clock,
		func() bool { return rig.paused })

	return rig
}

// seedHealthyCluster puts a healthy two-node cluster into the fake: the namespaces and version the snapshot
// reads, a control-plane node and a worker, and their pods.
func seedHealthyCluster(api *fakeAPI) {
	api.seed("/api/v1/namespaces/kube-system", map[string]any{"metadata": map[string]any{"name": "kube-system", "uid": testKubeSystemUID}})
	api.seed("/version", map[string]any{"major": "1", "minor": "31", "gitVersion": testClusterVersion})
	api.seed(namespacePath, namespaceManifest())

	api.setNodes([]map[string]any{
		testNode("cp-1", withLabels(map[string]string{
			"node-role.kubernetes.io/master":        "true",
			"node-role.kubernetes.io/control-plane": "",
			"node-role.kubernetes.io/etcd":          "true",
		})),
		testNode("worker-1"),
	})
	api.setPods([]map[string]any{
		testPod("cp-1", "Running"),
		testPod("cp-1", "Pending"),
		testPod("cp-1", "Unknown"),
		testPod("cp-1", "Succeeded"),
		testPod("worker-1", "Running"),
		testPod("worker-1", "Running"),
		testPod("worker-1", "Failed"),
		testPod("", "Pending"),
		testPod("ghost", "Running"),
	})
}

func (r *snapshotRig) newClient() (*kube.Client, error) {
	return kube.New(kube.Config{Server: r.api.url(), CAData: r.caPEM, Token: r.token})
}

// collect runs one collection as the holder.
func (r *snapshotRig) collect() {
	r.t.Helper()

	r.snaps.collect(context.Background(), r.lead, r.newClient)
}

// getCount is how many GET requests the fake served for path.
func (r *snapshotRig) getCount(path string) int {
	return r.api.requestCount(http.MethodGet, path)
}

// testNode is a healthy, schedulable node as the API returns it, which edits may change.
func testNode(name string, edits ...func(node map[string]any)) map[string]any {
	node := map[string]any{
		"metadata": map[string]any{"name": name, "labels": map[string]any{"kubernetes.io/hostname": name}},
		"spec":     map[string]any{},
		"status": map[string]any{
			"conditions": []any{
				map[string]any{"type": "MemoryPressure", "status": "False", "lastTransitionTime": "2026-10-11T09:00:00Z"},
				map[string]any{"type": "DiskPressure", "status": "False", "lastTransitionTime": "2026-10-11T09:00:00Z"},
				map[string]any{"type": "PIDPressure", "status": "False", "lastTransitionTime": "2026-10-11T09:00:00Z"},
				map[string]any{"type": "Ready", "status": "True", "lastTransitionTime": "2026-10-11T10:00:00Z"},
			},
			"nodeInfo": map[string]any{
				"machineID":               "machine-" + name,
				"kubeletVersion":          "v1.31.4+k3s1",
				"containerRuntimeVersion": "containerd://1.7.23-k3s2",
			},
			"allocatable": map[string]any{"pods": "110", "cpu": "4"},
		},
	}

	for _, edit := range edits {
		edit(node)
	}

	return node
}

func withLabels(labels map[string]string) func(map[string]any) {
	return func(node map[string]any) {
		existing := node["metadata"].(map[string]any)["labels"].(map[string]any)
		for key, value := range labels {
			existing[key] = value
		}
	}
}

func unschedulable(node map[string]any) {
	node["spec"].(map[string]any)["unschedulable"] = true
}

// withCondition sets the status and transition time of one condition, adding it when the node has none of that
// type.
func withCondition(condType string, status string, transitioned string) func(map[string]any) {
	return func(node map[string]any) {
		nodeStatus := node["status"].(map[string]any)
		conditions := nodeStatus["conditions"].([]any)

		for _, existing := range conditions {
			condition := existing.(map[string]any)
			if condition["type"] == condType {
				condition["status"] = status
				condition["lastTransitionTime"] = transitioned

				return
			}
		}

		nodeStatus["conditions"] = append(conditions, map[string]any{"type": condType, "status": status, "lastTransitionTime": transitioned})
	}
}

func withoutConditions(node map[string]any) {
	node["status"].(map[string]any)["conditions"] = []any{}
}

func withAllocatablePods(value any) func(map[string]any) {
	return func(node map[string]any) {
		node["status"].(map[string]any)["allocatable"] = map[string]any{"pods": value}
	}
}

func testPod(nodeName string, phase string) map[string]any {
	spec := map[string]any{"containers": []any{}}
	if nodeName != "" {
		spec["nodeName"] = nodeName
	}

	return map[string]any{
		"metadata": map[string]any{"name": "pod"},
		"spec":     spec,
		"status":   map[string]any{"phase": phase},
	}
}

// nodeByName finds a node of a record.
func nodeByName(t *testing.T, record *pb.KubernetesClusterRecord, name string) *pb.KubernetesClusterNode {
	t.Helper()

	for _, node := range record.Nodes {
		if node.Name == name {
			return node
		}
	}

	t.Fatalf("snapshot has no node %q; it lists %d nodes", name, len(record.Nodes))

	return nil
}

// requireRecord fails the test unless got equals want, showing both.
func requireRecord(t *testing.T, got *pb.KubernetesClusterRecord, want *pb.KubernetesClusterRecord) {
	t.Helper()

	if proto.Equal(got, want) == false {
		t.Fatalf("snapshot differs.\n got: %s\nwant: %s", protojson.Format(got), protojson.Format(want))
	}
}

// Intent: a holder's snapshot of a healthy cluster carries everything the server needs, in the shape it reads:
// the cluster identity, version and role version, the CA fingerprint, the Lease tenure that fences it, and every
// node sorted by name with its roles, readiness, capacity and the pods placed on it, counting only pods that are
// not finished and are on a node.
func TestSnapshot_ACollectedClusterCarriesEverythingTheServerReads(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued = %d, want 1", rig.queue.count())
	}

	if rig.queue.items[0].itemType != db.TelemetryKubernetesCluster {
		t.Errorf("queued as type %d, want the cluster snapshot type %d", rig.queue.items[0].itemType, db.TelemetryKubernetesCluster)
	}

	if rig.queue.items[0].id == "" {
		t.Error("the snapshot was queued without an id")
	}

	requireRecord(t, rig.queue.last(t), &pb.KubernetesClusterRecord{
		KubeSystemUid:    testKubeSystemUID,
		ClusterVersion:   testClusterVersion,
		RoleVersion:      CompiledRoleVersion,
		RoleOutdated:     false,
		CaFingerprint:    testFingerprint,
		LeaseTransitions: testTransitions,
		LeaseUid:         testLeaseUID,
		LeaseCreatedAt:   testLeaseCreated,
		Nodes: []*pb.KubernetesClusterNode{
			{
				Name:                    "cp-1",
				Roles:                   []string{"control-plane", "etcd", "master"},
				ReadyStatus:             "True",
				ReadySince:              "2026-10-11T10:00:00Z",
				MachineId:               "machine-cp-1",
				KubeletVersion:          "v1.31.4+k3s1",
				ContainerRuntimeVersion: "containerd://1.7.23-k3s2",
				PodCount:                3,
				PodCapacity:             110,
			},
			{
				Name:                    "worker-1",
				ReadyStatus:             "True",
				ReadySince:              "2026-10-11T10:00:00Z",
				MachineId:               "machine-worker-1",
				KubeletVersion:          "v1.31.4+k3s1",
				ContainerRuntimeVersion: "containerd://1.7.23-k3s2",
				PodCount:                2,
				PodCapacity:             110,
			},
		},
	})

	if got := rig.snaps.consecutiveErrors(); got != 0 {
		t.Errorf("consecutive error snapshots = %d, want 0", got)
	}
}

// Intent: the cluster is read as the reporter, read-only, and with the paging it is asked for: nodes and pods in
// pages of 200, and pods only those that have not finished.
func TestSnapshot_ListsNodesAndPodsInPagesOf200WithoutFinishedPods(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()

	queries := map[string]url.Values{}

	for _, request := range rig.api.requestLog() {
		if request.Principal != "reporter" {
			t.Errorf("%s %s was made as %q, want the reporter", request.Method, request.Path, request.Principal)
		}

		if request.Method != http.MethodGet {
			t.Errorf("%s %s: a collection must only read", request.Method, request.Path)
		}

		query, err := url.ParseQuery(request.RawQuery)
		if err != nil {
			t.Fatalf("query %q: %v", request.RawQuery, err)
		}

		queries[request.Path] = query
	}

	for _, path := range []string{"/api/v1/nodes", "/api/v1/pods"} {
		if got := queries[path].Get("limit"); got != "200" {
			t.Errorf("GET %s was sent with limit %q, want 200", path, got)
		}
	}

	if got, want := queries["/api/v1/pods"].Get("fieldSelector"), "status.phase!=Succeeded,status.phase!=Failed"; got != want {
		t.Errorf("the pod list was asked for field selector %q, want %q", got, want)
	}

	if got := queries["/api/v1/nodes"].Get("fieldSelector"); got != "" {
		t.Errorf("the node list was asked for field selector %q, want none", got)
	}
}

// Intent: pods are counted across pages: a cluster with more pods than one page holds is counted whole, so the
// pod count of a busy node is not the count of the first 200.
func TestSnapshot_CountsPodsAcrossTwoPages(t *testing.T) {
	rig := newSnapshotRig(t)

	var pods []map[string]any

	for i := 0; i < 130; i++ {
		pods = append(pods, testPod("cp-1", "Running"))
	}

	for i := 0; i < 120; i++ {
		pods = append(pods, testPod("worker-1", "Running"))
	}

	pods = append(pods, testPod("worker-1", "Succeeded"))
	rig.api.setPods(pods)

	rig.collect()

	record := rig.queue.last(t)

	if got := nodeByName(t, record, "cp-1").PodCount; got != 130 {
		t.Errorf("cp-1 pod count = %d, want 130", got)
	}

	if got := nodeByName(t, record, "worker-1").PodCount; got != 120 {
		t.Errorf("worker-1 pod count = %d, want 120 (the finished pod does not count)", got)
	}

	var podPages []recordedRequest

	for _, request := range rig.api.requestLog() {
		if request.Path == "/api/v1/pods" {
			podPages = append(podPages, request)
		}
	}

	if len(podPages) != 2 {
		t.Fatalf("pod list requests = %d, want 2 pages", len(podPages))
	}

	if strings.Contains(podPages[0].RawQuery, "continue=") || strings.Contains(podPages[1].RawQuery, "continue=") == false {
		t.Errorf("the pages were fetched with %q then %q, want the second to continue the first", podPages[0].RawQuery, podPages[1].RawQuery)
	}
}

// Intent: when the cluster expires the continue token and the pod list starts over, the pods the abandoned pass
// counted are forgotten, so no node is counted twice.
func TestSnapshot_ARestartedPodListDoesNotCountTwice(t *testing.T) {
	rig := newSnapshotRig(t)

	var pods []map[string]any

	for i := 0; i < 250; i++ {
		pods = append(pods, testPod("cp-1", "Running"))
	}

	rig.api.setPods(pods)
	rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/pods", status: http.StatusGone, reason: "Expired", message: "continue token expired", skip: 1})

	rig.collect()

	if got := nodeByName(t, rig.queue.last(t), "cp-1").PodCount; got != 250 {
		t.Errorf("cp-1 pod count after a restarted list = %d, want 250", got)
	}
}

// Intent: a node list that is restarted forgets the nodes the abandoned pass collected, so a node that was deleted
// while the list was being read is not reported from the pass that saw it.
func TestSnapshot_ARestartedNodeListKeepsOnlyTheFinalPass(t *testing.T) {
	rig := newSnapshotRig(t)

	var nodes []map[string]any

	for i := 0; i < 250; i++ {
		nodes = append(nodes, testNode(fmt.Sprintf("node-%03d", i)))
	}

	rig.api.setNodes(nodes)
	rig.api.inject(fault{
		method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusGone, reason: "Expired", message: "continue token expired", skip: 1,
		// The first node is deleted after the first page was served and before the list starts over.
		onFire: func(api *fakeAPI) { api.nodes = api.nodes[1:] },
	})

	rig.collect()

	record := rig.queue.last(t)

	if got := len(record.Nodes); got != 249 {
		t.Fatalf("nodes reported after a restarted list = %d, want the 249 that were left", got)
	}

	for _, node := range record.Nodes {
		if node.Name == "node-000" {
			t.Fatal("the deleted node was reported from the abandoned pass")
		}
	}
}

// Intent: roles come from the presence of node-role labels, whatever their values, as the sorted suffixes; a bare
// prefix and other labels are not roles.
func TestSnapshot_RolesAreTheSortedSuffixesOfNodeRoleLabels(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.setNodes([]map[string]any{
		testNode("n1", withLabels(map[string]string{
			"node-role.kubernetes.io/worker":        "",
			"node-role.kubernetes.io/control-plane": "false",
			"node-role.kubernetes.io/":              "bare",
			"node-role.kubernetes.io.extra/x":       "y",
			"kubernetes.io/role":                    "worker",
		})),
		testNode("n2"),
	})

	rig.collect()

	record := rig.queue.last(t)

	if got, want := strings.Join(nodeByName(t, record, "n1").Roles, ","), "control-plane,worker"; got != want {
		t.Errorf("roles = %q, want %q", got, want)
	}

	if got := nodeByName(t, record, "n2").Roles; len(got) != 0 {
		t.Errorf("roles of an unlabelled node = %v, want none", got)
	}
}

// Intent: only trouble is reported as a condition: pressure and network conditions whose status is not False,
// each with its transition time, in a fixed order. Ready is reported as the node's readiness, and conditions of
// other kinds are not carried.
func TestSnapshot_ConditionsAreOnlyThePressureAndNetworkOnesThatAreNotFalse(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.setNodes([]map[string]any{
		testNode("n1",
			withCondition("PIDPressure", "True", "2026-10-11T11:30:00Z"),
			withCondition("MemoryPressure", "Unknown", "2026-10-11T11:00:00Z"),
			withCondition("NetworkUnavailable", "True", "2026-10-11T11:15:00Z"),
			withCondition("KernelDeadlock", "True", "2026-10-11T11:45:00Z"),
			withCondition("Ready", "False", "2026-10-11T12:00:00Z")),
		testNode("n2"),
	})

	rig.collect()

	record := rig.queue.last(t)
	trouble := nodeByName(t, record, "n1")

	if trouble.ReadyStatus != "False" || trouble.ReadySince != "2026-10-11T12:00:00Z" {
		t.Errorf("readiness = %q since %q, want False since 2026-10-11T12:00:00Z", trouble.ReadyStatus, trouble.ReadySince)
	}

	var got []string

	for _, condition := range trouble.Conditions {
		got = append(got, fmt.Sprintf("%s=%s@%s", condition.Type, condition.Status, condition.LastTransitionTime))
	}

	want := []string{
		"MemoryPressure=Unknown@2026-10-11T11:00:00Z",
		"NetworkUnavailable=True@2026-10-11T11:15:00Z",
		"PIDPressure=True@2026-10-11T11:30:00Z",
	}

	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("conditions = %v, want %v", got, want)
	}

	if healthy := nodeByName(t, record, "n2"); len(healthy.Conditions) != 0 {
		t.Errorf("a healthy node reports conditions %v, want none", healthy.Conditions)
	}
}

// Intent: a node that reports no Ready condition at all is not ready as far as anyone can tell, and says so
// rather than sending an empty status, and an unschedulable node is reported as one.
func TestSnapshot_ANodeWithoutAReadyConditionIsUnknownAndCordonIsReported(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.setNodes([]map[string]any{testNode("n1", withoutConditions, unschedulable), testNode("n2")})

	rig.collect()

	record := rig.queue.last(t)
	node := nodeByName(t, record, "n1")

	if node.ReadyStatus != "Unknown" || node.ReadySince != "" {
		t.Errorf("readiness = %q since %q, want Unknown with no time", node.ReadyStatus, node.ReadySince)
	}

	if node.Unschedulable == false {
		t.Error("a cordoned node is not reported unschedulable")
	}

	if nodeByName(t, record, "n2").Unschedulable {
		t.Error("a schedulable node is reported unschedulable")
	}
}

// Intent: the pod capacity is what the node allocates, read from the quantity string, and is 0 when the node
// does not say or says something that is not a count.
func TestSnapshot_PodCapacityIsParsedFromAllocatablePods(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.setNodes([]map[string]any{
		testNode("a-string", withAllocatablePods("250")),
		testNode("b-number", withAllocatablePods(64)),
		testNode("c-missing", func(node map[string]any) { node["status"].(map[string]any)["allocatable"] = map[string]any{"cpu": "4"} }),
		testNode("d-garbage", withAllocatablePods("many")),
		testNode("e-negative", withAllocatablePods("-5")),
	})

	rig.collect()

	record := rig.queue.last(t)

	for name, want := range map[string]int32{"a-string": 250, "b-number": 64, "c-missing": 0, "d-garbage": 0, "e-negative": 0} {
		if got := nodeByName(t, record, name).PodCapacity; got != want {
			t.Errorf("%s pod capacity = %d, want %d", name, got, want)
		}
	}
}

// Intent: the Lease's role-version annotation says how old the cluster's role is. A version below the one this
// agent carries is outdated, and a Lease with no usable annotation counts as version 0; a newer one is not.
func TestSnapshot_RoleOutdatedFollowsTheLeaseAnnotation(t *testing.T) {
	tests := []struct {
		name         string
		annotation   any
		wantVersion  int32
		wantOutdated bool
	}{
		{"current version", "1", 1, false},
		{"older version", "0", 0, true},
		{"newer version", "2", 2, false},
		{"missing annotation", nil, 0, true},
		{"unreadable annotation", "newest", 0, true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rig := newSnapshotRig(t)

			lease := leaseManifest(false)
			if tt.annotation != nil {
				lease["metadata"].(map[string]any)["annotations"] = map[string]any{roleVersionAnnotation: tt.annotation}
			}

			rig.api.seed(leasePath, lease)

			rig.collect()

			record := rig.queue.last(t)

			if record.RoleVersion != tt.wantVersion || record.RoleOutdated != tt.wantOutdated {
				t.Errorf("role version %d outdated %v, want %d and %v", record.RoleVersion, record.RoleOutdated, tt.wantVersion, tt.wantOutdated)
			}

			if record.ReporterError != "" || len(record.Nodes) != 2 {
				t.Errorf("an outdated role must not stop collection: error %q, %d nodes", record.ReporterError, len(record.Nodes))
			}
		})
	}
}

// Intent: every way the cluster can fail the collection yields an error snapshot with exactly one reason and no
// nodes, still stamped with the tenure that fences it and the CA fingerprint, and counted for the health gate:
// a refused token or a refused permission is credentials_rejected, a certificate the stored CA does not vouch for
// is tls_ca_changed, and anything else is api_unreachable.
func TestSnapshot_EachFailureBecomesAnErrorSnapshotWithItsReason(t *testing.T) {
	tests := []struct {
		name    string
		arrange func(rig *snapshotRig)
		want    string
	}{
		{"token the cluster never issued", func(rig *snapshotRig) { rig.token = "a-token-the-cluster-never-issued" }, reasonCredentialsRejected},
		{"permission refused on the nodes", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusForbidden, reason: "Forbidden", message: "nodes is forbidden"})
		}, reasonCredentialsRejected},
		{"permission refused on the pods", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/pods", status: http.StatusForbidden, reason: "Forbidden", message: "pods is forbidden"})
		}, reasonCredentialsRejected},
		{"certificate the stored CA does not vouch for", func(rig *snapshotRig) { rig.caPEM = unrelatedCAPEM(rig.t) }, reasonTLSCAChanged},
		{"server error on the version", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/version", status: http.StatusInternalServerError, reason: "InternalError", message: "boom"})
		}, reasonAPIUnreachable},
		{"server error on the node list", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusInternalServerError, reason: "InternalError", message: "boom"})
		}, reasonAPIUnreachable},
		{"server error on the pod list", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/pods", status: http.StatusServiceUnavailable, reason: "ServiceUnavailable", message: "busy"})
		}, reasonAPIUnreachable},
		{"missing namespace", func(rig *snapshotRig) {
			rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/namespaces/kube-system", status: http.StatusNotFound, reason: "NotFound", message: "gone"})
		}, reasonAPIUnreachable},
		{"server gone", func(rig *snapshotRig) { rig.api.server.Close() }, reasonAPIUnreachable},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rig := newSnapshotRig(t)
			tt.arrange(rig)

			rig.collect()

			if rig.queue.count() != 1 {
				t.Fatalf("snapshots queued = %d, want 1", rig.queue.count())
			}

			requireRecord(t, rig.queue.last(t), &pb.KubernetesClusterRecord{
				ReporterError:    tt.want,
				CaFingerprint:    testFingerprint,
				LeaseTransitions: testTransitions,
				LeaseUid:         testLeaseUID,
				LeaseCreatedAt:   testLeaseCreated,
			})

			if got := rig.snaps.consecutiveErrors(); got != 1 {
				t.Errorf("consecutive error snapshots = %d, want 1", got)
			}
		})
	}
}

// Intent: a namespace that is being deleted means the reporter is being uninstalled, and is reported as such
// instead of a cluster snapshot, without listing the cluster.
func TestSnapshot_ATerminatingReporterNamespaceIsReportedAndNothingIsListed(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.markNamespaceTerminating(100)

	rig.collect()

	requireRecord(t, rig.queue.last(t), &pb.KubernetesClusterRecord{
		ReporterError:    reasonNamespaceTerminating,
		CaFingerprint:    testFingerprint,
		LeaseTransitions: testTransitions,
		LeaseUid:         testLeaseUID,
		LeaseCreatedAt:   testLeaseCreated,
	})

	for _, path := range []string{"/api/v1/nodes", "/api/v1/pods"} {
		if got := rig.getCount(path); got != 0 {
			t.Errorf("GET %s made %d times while the namespace is terminating, want 0", path, got)
		}
	}

	if got := rig.snaps.consecutiveErrors(); got != 1 {
		t.Errorf("consecutive error snapshots = %d, want 1", got)
	}
}

// Intent: the health gate counts error snapshots in a row. Each one adds to the count, a good snapshot clears it,
// and the end of a tenure clears it too, so a holder released for failing is not held to its old failures.
func TestSnapshot_ConsecutiveErrorsCountClearOnSuccessAndOnTheEndOfATenure(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.inject(fault{method: http.MethodGet, path: "/version", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: 2})

	for want := 1; want <= 2; want++ {
		rig.collect()

		if got := rig.snaps.consecutiveErrors(); got != want {
			t.Fatalf("consecutive errors after %d failures = %d", want, got)
		}
	}

	rig.collect()

	if got := rig.snaps.consecutiveErrors(); got != 0 {
		t.Errorf("consecutive errors after a good snapshot = %d, want 0", got)
	}

	rig.api.inject(fault{method: http.MethodGet, path: "/version", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: 2})
	rig.collect()
	rig.collect()

	if got := rig.snaps.consecutiveErrors(); got != 2 {
		t.Fatalf("consecutive errors = %d, want 2", got)
	}

	rig.snaps.tenureEnded(nil)

	if got := rig.snaps.consecutiveErrors(); got != 0 {
		t.Errorf("consecutive errors after the tenure ended = %d, want 0", got)
	}
}

// Intent: a snapshot is sent when it changed or when it has not been sent for half an hour, so an unchanged
// cluster is heard from often enough to stay fresh without being resent every collection. The half hour is
// measured on the injected clock, and runs from the last send.
func TestSnapshot_ASnapshotIsSentWhenItChangesOrHalfAnHourPasses(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()
	rig.collect()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued after two identical collections = %d, want 1", rig.queue.count())
	}

	rig.clock.Advance(snapshotHeartbeat - time.Second)
	rig.collect()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued one second before the heartbeat = %d, want 1", rig.queue.count())
	}

	rig.clock.Advance(time.Second)
	rig.collect()

	if rig.queue.count() != 2 {
		t.Fatalf("snapshots queued at the heartbeat = %d, want 2", rig.queue.count())
	}

	rig.clock.Advance(snapshotHeartbeat - time.Second)
	rig.collect()

	if rig.queue.count() != 2 {
		t.Errorf("snapshots queued a second before the next heartbeat = %d, want the half hour to run from the last send", rig.queue.count())
	}
}

// Intent: the heartbeat is half an hour and a collection is a quarter of one, which keeps a snapshot well inside
// the 90 minutes after which the server treats a reporter as gone.
func TestSnapshot_TimingConstants(t *testing.T) {
	if snapshotInterval != 15*time.Minute {
		t.Errorf("snapshotInterval = %v, want 15 minutes", snapshotInterval)
	}

	if snapshotHeartbeat != 30*time.Minute {
		t.Errorf("snapshotHeartbeat = %v, want 30 minutes", snapshotHeartbeat)
	}

	if snapshotMaxBytes != 256*1024 {
		t.Errorf("snapshotMaxBytes = %d, want 256 KB", snapshotMaxBytes)
	}

	if snapshotPageSize != 200 {
		t.Errorf("snapshotPageSize = %d, want 200", snapshotPageSize)
	}
}

// Intent: change detection looks at the cluster, not at its traffic: pod counts move all the time and must not
// make a snapshot go out, while any change to a node, its state or its place in the cluster must.
func TestSnapshot_PodCountsDoNotCountAsAChangeButNodeStateDoes(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()

	pods := append(rig.api.pods, testPod("cp-1", "Running"), testPod("worker-1", "Running"))
	rig.api.setPods(pods)
	rig.collect()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued after pod counts moved = %d, want 1", rig.queue.count())
	}

	changes := []struct {
		name  string
		nodes []map[string]any
	}{
		{"a node cordoned", []map[string]any{testNode("cp-1", withLabels(map[string]string{"node-role.kubernetes.io/master": "true", "node-role.kubernetes.io/control-plane": "", "node-role.kubernetes.io/etcd": "true"})), testNode("worker-1", unschedulable)}},
		{"a node under pressure", []map[string]any{testNode("cp-1", withLabels(map[string]string{"node-role.kubernetes.io/master": "true", "node-role.kubernetes.io/control-plane": "", "node-role.kubernetes.io/etcd": "true"})), testNode("worker-1", withCondition("DiskPressure", "True", "2026-10-11T13:00:00Z"))}},
		{"a node not ready since later", []map[string]any{testNode("cp-1", withLabels(map[string]string{"node-role.kubernetes.io/master": "true", "node-role.kubernetes.io/control-plane": "", "node-role.kubernetes.io/etcd": "true"})), testNode("worker-1", withCondition("Ready", "False", "2026-10-11T13:30:00Z"))}},
		{"a node removed", []map[string]any{testNode("cp-1", withLabels(map[string]string{"node-role.kubernetes.io/master": "true", "node-role.kubernetes.io/control-plane": "", "node-role.kubernetes.io/etcd": "true"}))}},
	}

	for i, change := range changes {
		rig.api.setNodes(change.nodes)
		rig.collect()

		if got, want := rig.queue.count(), i+2; got != want {
			t.Fatalf("snapshots queued after %s = %d, want %d", change.name, got, want)
		}
	}
}

// Intent: a change in the cluster's own identity or version is a change: a new Kubernetes version goes out.
func TestSnapshot_AClusterVersionChangeIsSent(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()
	rig.api.seed("/version", map[string]any{"gitVersion": "v1.32.0+k3s1"})
	rig.collect()

	if rig.queue.count() != 2 {
		t.Fatalf("snapshots queued = %d, want 2", rig.queue.count())
	}

	if got := rig.queue.last(t).ClusterVersion; got != "v1.32.0+k3s1" {
		t.Errorf("cluster version = %q, want v1.32.0+k3s1", got)
	}
}

// Intent: the lease epoch and transition count are part of what is compared, so the first snapshot of a new
// tenure always goes out even when the cluster has not changed: a new holder, or the same Lease re-created, is
// not mistaken for the old one's last snapshot.
func TestSnapshot_ANewTenureOrLeaseEpochIsAChange(t *testing.T) {
	changes := map[string]func(l *fakeLeadership){
		"transitions": func(l *fakeLeadership) { l.transitions++ },
		"lease uid":   func(l *fakeLeadership) { l.uid = "lease-uid-2" },
		"created at":  func(l *fakeLeadership) { l.created = "2026-10-12T08:00:00Z" },
	}

	for name, change := range changes {
		t.Run(name, func(t *testing.T) {
			rig := newSnapshotRig(t)

			rig.collect()
			change(rig.lead)
			rig.collect()

			if rig.queue.count() != 2 {
				t.Fatalf("snapshots queued = %d, want 2", rig.queue.count())
			}
		})
	}
}

// Intent: the first snapshot after each acquisition is always sent: forgetting what was last sent makes an
// unchanged cluster go out again, as it must when the queued copy of the last one was dropped with the Lease.
func TestSnapshot_ForgettingTheLastSentSnapshotSendsAnUnchangedOneAgain(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()
	rig.collect()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued = %d, want 1", rig.queue.count())
	}

	rig.snaps.forgetLastSent()
	rig.collect()

	if rig.queue.count() != 2 {
		t.Errorf("snapshots queued after forgetting = %d, want 2", rig.queue.count())
	}

	rig.collect()

	if rig.queue.count() != 2 {
		t.Errorf("snapshots queued after the next unchanged collection = %d, want still 2", rig.queue.count())
	}
}

// Intent: the end of a tenure forgets the last snapshot too, whether or not an acquisition is announced, so a
// holder never skips a snapshot because its predecessor tenure sent the same one.
func TestSnapshot_TheEndOfATenureForgetsTheLastSentSnapshot(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()
	rig.snaps.tenureEnded(nil)
	rig.collect()

	if rig.queue.count() != 2 {
		t.Errorf("snapshots queued = %d, want the snapshot after a tenure to be sent again", rig.queue.count())
	}
}

// Intent: the end of a tenure runs what drops the queued snapshots.
func TestSnapshot_TheEndOfATenureDropsTheQueuedSnapshots(t *testing.T) {
	rig := newSnapshotRig(t)

	dropped := 0

	rig.snaps.tenureEnded(func() { dropped++ })

	if dropped != 1 {
		t.Errorf("drop ran %d times, want 1", dropped)
	}
}

// Intent: a standby never collects: with no leadership nothing is requested from the cluster and nothing is
// queued, so a cluster is read by one host only.
func TestSnapshot_AStandbyNeverCollects(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.lead.ok = false

	rig.collect()

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests by a standby = %d, want none", got)
	}

	if rig.queue.count() != 0 {
		t.Errorf("snapshots queued by a standby = %d, want none", rig.queue.count())
	}

	if got := rig.snaps.consecutiveErrors(); got != 0 {
		t.Errorf("consecutive errors of a standby = %d, want 0", got)
	}
}

// Intent: a paused host collects nothing, as every other collector is gated by the pause.
func TestSnapshot_APausedHostCollectsNothing(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.paused = true

	rig.collect()

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests while paused = %d, want none", got)
	}

	if rig.queue.count() != 0 {
		t.Errorf("snapshots queued while paused = %d, want none", rig.queue.count())
	}

	if rig.lead.calls != 0 {
		t.Errorf("leadership was asked %d times while paused, want not at all", rig.lead.calls)
	}
}

// Intent: a pause that begins while the cluster is being read keeps the snapshot out of the queue, which a pause
// has just emptied.
func TestSnapshot_APauseDuringCollectionKeepsTheSnapshotOutOfTheQueue(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.lead.onCall = func(call int) {
		if call == 2 {
			rig.paused = true
		}
	}

	rig.collect()

	if rig.queue.count() != 0 {
		t.Errorf("snapshots queued after a pause began = %d, want none", rig.queue.count())
	}
}

// Intent: the stamp that fences a snapshot comes from one reading of the leadership, taken before the cluster is
// read, and the snapshot is queued only if the same tenure still holds afterwards. One that lost the Lease while
// it was being collected is dropped, and so is one that spans a loss and a new acquisition.
func TestSnapshot_ASnapshotIsQueuedOnlyIfTheSameTenureStillHolds(t *testing.T) {
	tests := []struct {
		name   string
		onCall func(l *fakeLeadership, call int)
	}{
		{"leadership lost", func(l *fakeLeadership, call int) {
			if call == 2 {
				l.ok = false
			}
		}},
		{"won again with a new transition count", func(l *fakeLeadership, call int) {
			if call == 2 {
				l.transitions++
			}
		}},
		{"won again in a re-created lease", func(l *fakeLeadership, call int) {
			if call == 2 {
				l.uid = "lease-uid-2"
			}
		}},
		{"won again in a lease created later", func(l *fakeLeadership, call int) {
			if call == 2 {
				l.created = "2026-10-12T08:00:00Z"
			}
		}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			rig := newSnapshotRig(t)
			rig.lead.onCall = func(call int) { tt.onCall(rig.lead, call) }

			rig.collect()

			if rig.queue.count() != 0 {
				t.Errorf("snapshots queued = %d, want the snapshot of an ended tenure dropped", rig.queue.count())
			}

			if got := rig.snaps.consecutiveErrors(); got != 0 {
				t.Errorf("consecutive errors = %d, want a dropped snapshot to leave the count alone", got)
			}

			if rig.lead.calls != 2 {
				t.Errorf("leadership was read %d times, want once to stamp the snapshot and once to confirm it", rig.lead.calls)
			}
		})
	}
}

// Intent: a failure that arrives after the Lease was lost says nothing about this host as a holder, so it is not
// queued and does not count toward the next tenure's gate.
func TestSnapshot_AFailureAfterLosingTheLeaseIsNotCounted(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.api.inject(fault{method: http.MethodGet, path: "/version", status: http.StatusInternalServerError, reason: "InternalError", message: "boom"})

	rig.lead.onCall = func(call int) {
		if call == 2 {
			rig.lead.ok = false
		}
	}

	rig.collect()

	if rig.queue.count() != 0 || rig.snaps.consecutiveErrors() != 0 {
		t.Errorf("queued %d, counted %d errors; want neither after the Lease was lost", rig.queue.count(), rig.snaps.consecutiveErrors())
	}
}

// Intent: the end of a tenure that is announced in the instant between the confirmation and the enqueue still
// stops the snapshot: whatever ended it is also what drops the queue, and that must not be followed by a late
// insert of the dying tenure's snapshot.
func TestSnapshot_ATenureThatEndsBetweenTheConfirmationAndTheEnqueueQueuesNothing(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.lead.onCall = func(call int) {
		if call == 2 {
			rig.snaps.tenureEnded(nil)
		}
	}

	rig.collect()

	if rig.queue.count() != 0 {
		t.Errorf("snapshots queued = %d, want none: the tenure ended before the enqueue", rig.queue.count())
	}
}

// Intent: a snapshot that could not be queued is not remembered as sent, so the next collection tries again even
// though nothing about the cluster changed.
func TestSnapshot_AFailedEnqueueIsRetriedByTheNextCollection(t *testing.T) {
	rig := newSnapshotRig(t)
	rig.queue.err = errors.New("disk full")

	rig.collect()

	if rig.queue.count() != 0 {
		t.Fatalf("snapshots queued = %d, want none while the queue fails", rig.queue.count())
	}

	rig.queue.err = nil
	rig.collect()

	if rig.queue.count() != 1 {
		t.Errorf("snapshots queued after the queue recovered = %d, want 1", rig.queue.count())
	}
}

// Intent: the CA fingerprint is the host's own, from local detection, and a host that cannot tell sends none
// rather than failing: the server never creates a cluster from a snapshot without one.
func TestSnapshot_TheCAFingerprintComesFromLocalDetection(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.facts = kubelocal.Facts{CaFingerprint: "another-fingerprint"}
	rig.collect()

	if got := rig.queue.last(t).CaFingerprint; got != "another-fingerprint" {
		t.Errorf("fingerprint = %q, want the one local detection gave", got)
	}

	rig = newSnapshotRig(t)
	rig.snaps.facts = nil
	rig.collect()

	if got := rig.queue.last(t).CaFingerprint; got != "" {
		t.Errorf("fingerprint without a detection source = %q, want none", got)
	}
}

// Intent: a cluster too large for the size cap is cut deterministically: nodes are sorted by name, the largest
// prefix that fits is kept, the snapshot says it was truncated, and what is queued fits both the encoded-proto cap
// and the queue's own payload limit. Every node is still read: the list is paged to the end first.
func TestSnapshot_ATwoThousandNodeClusterIsTruncatedByNameAtTheByteCap(t *testing.T) {
	rig := newSnapshotRig(t)

	const nodeCount = 2000

	var nodes []map[string]any

	// The nodes are served in reverse order so that sorting, not list order, decides what is kept.
	for i := nodeCount - 1; i >= 0; i-- {
		nodes = append(nodes, testNode(fmt.Sprintf("node-%04d", i), withLabels(map[string]string{"node-role.kubernetes.io/worker": ""}),
			withCondition("DiskPressure", "True", "2026-10-11T11:00:00Z")))
	}

	rig.api.setNodes(nodes)
	rig.collect()

	record := rig.queue.last(t)

	if record.Truncated == false {
		t.Fatal("a 2000 node snapshot was not marked truncated")
	}

	if size := proto.Size(record); size > snapshotMaxBytes {
		t.Errorf("encoded size = %d, want at most %d", size, snapshotMaxBytes)
	}

	if len(rig.queue.items[0].payload) > db.MaxPayloadBytes {
		t.Errorf("queued payload = %d bytes, want at most the queue's limit %d", len(rig.queue.items[0].payload), db.MaxPayloadBytes)
	}

	kept := len(record.Nodes)
	if (kept == 0) || (kept >= nodeCount) {
		t.Fatalf("nodes kept = %d, want some but not all of %d", kept, nodeCount)
	}

	t.Logf("kept %d of %d nodes: %d bytes encoded, %d bytes of JSON", kept, nodeCount, proto.Size(record), len(rig.queue.items[0].payload))

	names := make([]string, 0, kept)
	for _, node := range record.Nodes {
		names = append(names, node.Name)
	}

	if sort.StringsAreSorted(names) == false {
		t.Error("the kept nodes are not sorted by name")
	}

	for i, name := range names {
		if want := fmt.Sprintf("node-%04d", i); name != want {
			t.Fatalf("kept node %d is %q, want %q: the kept nodes must be the first by name", i, name, want)
		}
	}

	// One more node would not have fitted: the cut is as late as the cap allows.
	bigger := proto.Clone(record).(*pb.KubernetesClusterRecord)
	bigger.Nodes = append(bigger.Nodes, proto.Clone(record.Nodes[0]).(*pb.KubernetesClusterNode))
	bigger.Nodes[kept].Name = fmt.Sprintf("node-%04d", kept)

	if proto.Size(bigger) <= snapshotMaxBytes {
		t.Errorf("node %d would have fitted in %d bytes: truncation dropped more than it had to", kept, proto.Size(bigger))
	}

	if got := rig.getCount("/api/v1/nodes"); got != nodeCount/snapshotPageSize {
		t.Errorf("node list requests = %d, want %d pages of %d", got, nodeCount/snapshotPageSize, snapshotPageSize)
	}
}

// Intent: a cluster that fits is not truncated, and a record exactly at the cap is kept whole.
func TestSnapshot_AClusterThatFitsIsNotTruncated(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()

	if rig.queue.last(t).Truncated {
		t.Error("a two node snapshot was marked truncated")
	}
}

// Intent: truncation keeps the largest prefix that fits, at the boundary: a record that is exactly at the cap is
// whole, and one byte over loses exactly the nodes it must.
func TestFitRecord_KeepsTheLargestPrefixThatFits(t *testing.T) {
	build := func(n int) *pb.KubernetesClusterRecord {
		record := &pb.KubernetesClusterRecord{KubeSystemUid: "u"}

		for i := 0; i < n; i++ {
			record.Nodes = append(record.Nodes, &pb.KubernetesClusterNode{Name: fmt.Sprintf("n-%06d", i), ReadyStatus: "True"})
		}

		return record
	}

	whole := build(100)
	size := proto.Size(whole)

	exact := build(100)
	fitRecord(exact, size)

	if exact.Truncated || len(exact.Nodes) != 100 {
		t.Errorf("a record exactly at the cap: truncated %v, %d nodes; want it whole", exact.Truncated, len(exact.Nodes))
	}

	over := build(100)
	fitRecord(over, size-1)

	if over.Truncated == false || len(over.Nodes) >= 100 {
		t.Fatalf("a record one byte over the cap: truncated %v, %d nodes; want it cut", over.Truncated, len(over.Nodes))
	}

	if proto.Size(over) > size-1 {
		t.Errorf("the cut record is %d bytes, over the cap %d", proto.Size(over), size-1)
	}

	next := build(len(over.Nodes) + 1)
	next.Truncated = true

	if proto.Size(next) <= size-1 {
		t.Errorf("one more node would have fitted: %d bytes in a cap of %d", proto.Size(next), size-1)
	}

	tiny := build(5)
	fitRecord(tiny, 3)

	if tiny.Truncated == false || len(tiny.Nodes) != 0 {
		t.Errorf("a cap no node fits under: truncated %v, %d nodes; want an empty truncated list", tiny.Truncated, len(tiny.Nodes))
	}
}

// Intent: the snapshot is queued as JSON the sender decodes with protojson, whatever its 64-bit values, and a
// payload the real queue accepts: the whole path from collection to the queue works against the real store.
func TestSnapshot_TheQueuedPayloadIsAcceptedByTheRealQueue(t *testing.T) {
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open database: %v", err)
	}

	t.Cleanup(func() { database.Close() })

	store := db.NewStore(database, 0)
	rig := newSnapshotRig(t)
	rig.snaps.queue = store

	rig.collect()

	items, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 10)
	if err != nil || len(items) != 1 {
		t.Fatalf("dequeue: %v (%d items)", err, len(items))
	}

	var record pb.KubernetesClusterRecord
	if err := protojson.Unmarshal([]byte(items[0].Payload), &record); err != nil {
		t.Fatalf("the sender cannot read the queued payload: %v", err)
	}

	if record.LeaseTransitions != testTransitions || len(record.Nodes) != 2 {
		t.Errorf("record = %s", protojson.Format(&record))
	}

	// A second, changed snapshot replaces the first in the real queue.
	rig.api.setNodes([]map[string]any{testNode("only")})

	if _, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 10); err != nil {
		t.Fatalf("dequeue: %v", err)
	}

	rig.collect()
	rig.api.setNodes([]map[string]any{testNode("other")})
	rig.collect()

	left, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 10)
	if err != nil || len(left) != 1 {
		t.Fatalf("snapshots left in the queue = %d, %v; want only the latest", len(left), err)
	}

	if err := protojson.Unmarshal([]byte(left[0].Payload), &record); err != nil || record.Nodes[0].Name != "other" {
		t.Errorf("the snapshot left is %s (%v), want the one with node 'other'", protojson.Format(&record), err)
	}
}

// Intent: a collection cut short because the agent is stopping is abandoned quietly: it is neither queued nor
// counted as a failure of the cluster.
func TestSnapshot_ACollectionCancelledByShutdownIsAbandoned(t *testing.T) {
	rig := newSnapshotRig(t)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	rig.snaps.collect(ctx, rig.lead, rig.newClient)

	if rig.queue.count() != 0 || rig.snaps.consecutiveErrors() != 0 {
		t.Errorf("queued %d, counted %d errors; want neither for a cancelled collection", rig.queue.count(), rig.snaps.consecutiveErrors())
	}
}

// Intent: a client that cannot be built from the stored credentials is an unreachable cluster as far as the
// snapshot can tell, and is reported, not a crash.
func TestSnapshot_ACredentialSetThatBuildsNoClientIsReportedAsUnreachable(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.snaps.collect(context.Background(), rig.lead, func() (*kube.Client, error) { return nil, errors.New("no client") })

	if rig.queue.count() != 1 || rig.queue.last(t).ReporterError != reasonAPIUnreachable {
		t.Errorf("queued %d snapshots, last error %q; want one api_unreachable snapshot", rig.queue.count(), rig.queue.last(t).ReporterError)
	}
}

// Intent: the snapshot's event ids are fresh for every snapshot.
func TestSnapshot_EverySnapshotGetsItsOwnId(t *testing.T) {
	rig := newSnapshotRig(t)

	rig.collect()
	rig.snaps.forgetLastSent()
	rig.collect()

	if rig.queue.items[0].id == rig.queue.items[1].id {
		t.Errorf("two snapshots share the id %q", rig.queue.items[0].id)
	}
}
