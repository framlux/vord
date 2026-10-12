// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubelocal"
	"github.com/framlux/vord/internal/kubereporter"
	pb "github.com/framlux/vord/internal/proto/agent"
)

// This file runs the cluster reporter against a real k3s API server in a Podman container. It is built only with
// the k3s_integration tag, so a plain go test never starts it, and it needs Podman: without the podman binary on
// the PATH it skips. Run it with `make test-k3s`.
//
// Everything the reporter decides about time (the election's lease clock) runs on a fake clock the test moves.
// The waits that remain are real and bounded: the container starting, the cluster filling in a token, and a
// namespace finishing its deletion are things only the real cluster does.

const (
	k3sImage          = "docker.io/rancher/k3s:v1.33.13-k3s2"
	k3sKubeconfigFile = "/etc/rancher/k3s/k3s.yaml"

	// stepTimeout bounds how long the test waits for one reporter loop to take a tick.
	stepTimeout = 60 * time.Second

	// pollInterval is how often a real wait looks at the cluster.
	pollInterval = 100 * time.Millisecond

	// namespaceGoneTimeout bounds the wait for a deleted namespace to disappear.
	namespaceGoneTimeout = 3 * time.Minute

	// commandTimeout bounds one reporter command, as the agent's own executors do.
	commandTimeout = 150 * time.Second
)

// What the reporter installs, as the cluster sees it. The test names the objects itself so that it checks the
// compiled names rather than reusing them.
const (
	managedByLabel = "app.kubernetes.io/managed-by"
	managedByValue = "vord-agent"

	reporterNamespace     = "vord-system"
	tokenSecretPrefix     = "vord-reporter-token-"
	roleVersionAnnotation = "vord.framlux.io/role-version"
	rbacGroupPath         = "/apis/rbac.authorization.k8s.io/v1"

	namespacesPath          = "/api/v1/namespaces"
	namespacePath           = namespacesPath + "/" + reporterNamespace
	secretsPath             = namespacePath + "/secrets"
	configMapsPath          = namespacePath + "/configmaps"
	serviceAccountsPath     = namespacePath + "/serviceaccounts"
	serviceAccountPath      = serviceAccountsPath + "/vord-reporter"
	clusterRolesPath        = rbacGroupPath + "/clusterroles"
	clusterRolePath         = clusterRolesPath + "/vord-reporter"
	clusterRoleBindingsPath = rbacGroupPath + "/clusterrolebindings"
	clusterRoleBindingPath  = clusterRoleBindingsPath + "/vord-reporter"
	rolesPath               = rbacGroupPath + "/namespaces/" + reporterNamespace + "/roles"
	rolePath                = rolesPath + "/vord-reporter-lease"
	roleBindingsPath        = rbacGroupPath + "/namespaces/" + reporterNamespace + "/rolebindings"
	roleBindingPath         = roleBindingsPath + "/vord-reporter-lease"
	leasesPath              = "/apis/coordination.k8s.io/v1/namespaces/" + reporterNamespace + "/leases"
	leasePath               = leasesPath + "/vord-reporter"
)

// serverLine finds the server entry of a kubeconfig.
var serverLine = regexp.MustCompile(`(?m)^([ \t]*server:[ \t]*)\S+[ \t]*$`)

// TestK3sIntegration walks one k3s cluster through the whole life of the cluster reporter. The phases depend on
// each other, so the first one that fails ends the test.
func TestK3sIntegration(t *testing.T) {
	cluster := startK3s(t)
	clock := newFakeClock(time.Now().UTC().Truncate(time.Second))

	runCtx, stopRuns := context.WithCancel(context.Background())

	s := &scenario{
		cluster: cluster,
		clock:   clock,
		runCtx:  runCtx,
		a:       newCandidate(t, "a", cluster, clock),
		b:       newCandidate(t, "b", cluster, clock),
	}

	t.Cleanup(func() {
		stopRuns()
		s.a.stop(t)
		s.b.stop(t)
	})

	s.phase(t, "enrol_both_candidates_concurrently", s.enrolBoth)
	s.phase(t, "exactly_one_candidate_leads", s.elect)
	s.phase(t, "leader_snapshot_lists_the_node", s.snapshotAsLeader)
	s.phase(t, "failover_after_a_crash_without_release", s.failOver)
	s.phase(t, "paused_gate_releases_the_lease", s.releaseOnPause)
	s.phase(t, "deleted_token_secret_is_reported", s.revokeToken)
	s.phase(t, "uninstall_leaves_nothing_behind", s.uninstallBoth)
	s.phase(t, "re_enrol_starts_a_new_lease_epoch", s.reEnrol)
	s.phase(t, "stale_lease_write_conflicts", s.staleLeaseWrite)
	s.phase(t, "delete_preconditions_hold", s.deletePreconditions)
	s.phase(t, "missing_lease_is_not_found_for_the_reporter", s.missingLease)
	s.phase(t, "terminating_namespace", s.terminatingNamespace)
}

// scenario is what the phases share: the cluster, the fake clock and the two candidates.
type scenario struct {
	cluster *k3sCluster
	clock   *fakeClock
	runCtx  context.Context
	a, b    *candidate

	// holder and standby are the candidates as the last election left them.
	holder  *candidate
	standby *candidate

	// firstEpoch is the Lease as the first election found it.
	firstEpoch leaseView
	// lastTransitions is the transition count the first Lease epoch ended with.
	lastTransitions int64
	// secondEpoch is the Lease as the election after the re-enrol found it.
	secondEpoch leaseView
}

// phase runs one step of the scenario and ends the test when it fails.
func (s *scenario) phase(t *testing.T, name string, run func(t *testing.T)) {
	t.Helper()

	if t.Run(name, run) == false {
		t.FailNow()
	}
}

// enrolBoth enrols both candidates at the same moment. Both must succeed and the cluster must hold exactly one of
// every shared object and one token Secret each.
func (s *scenario) enrolBoth(t *testing.T) {
	enrolAll(t, s.a, s.b)

	idA, idB := s.a.instanceID(t), s.b.instanceID(t)
	if idA == idB {
		t.Fatalf("both candidates enrolled with instance id %q", idA)
	}

	for _, c := range []*candidate{s.a, s.b} {
		info, err := os.Stat(filepath.Join(c.dataDir, kubereporter.StateFileName))
		if err != nil {
			t.Fatalf("%s: state file: %v", c.name, err)
		}

		if info.Mode().Perm() != 0o600 {
			t.Errorf("%s: state file mode = %v, want 0600", c.name, info.Mode().Perm())
		}
	}

	s.cluster.requireInstalled(t, idA, idB)
}

// elect starts both candidates' loops at the same moment against the real API. Exactly one of them must end up
// holding the Lease, the other must be a standby, and the holder renews through the real compare-and-swap.
func (s *scenario) elect(t *testing.T) {
	start := make(chan struct{})

	s.a.start(s.runCtx, start)
	s.b.start(s.runCtx, start)

	close(start)

	eventually(t, stepTimeout, "a candidate to take the Lease", func() bool {
		lease, err := s.cluster.lease()

		return (err == nil) && (lease.holder != "")
	})

	lease := s.cluster.mustLease(t)
	s.holder, s.standby = s.candidatesByHolder(t, lease.holder)

	if lease.transitions != 1 {
		t.Errorf("leaseTransitions after the first election = %d, want 1", lease.transitions)
	}

	// Two ticks on the standby: the second is accepted only when the loop has finished the first step, so after
	// it every earlier step of the standby is done. At a fixed clock none of them can take a held Lease.
	s.standby.tick(t)
	s.standby.tick(t)

	after := s.cluster.mustLease(t)
	if (after.holder != lease.holder) || (after.transitions != 1) {
		t.Fatalf("the Lease changed hands without time passing: holder %q -> %q, transitions %d", lease.holder, after.holder, after.transitions)
	}

	if got := s.standby.queue.count(); got != 0 {
		t.Errorf("the standby queued %d snapshots, only the holder may", got)
	}

	// The holder renews against the real resourceVersion once the lease clock has moved.
	s.clock.Advance(20 * time.Second)
	renewedAt := s.clock.Now()

	s.holder.tick(t)
	s.holder.tick(t)

	eventually(t, stepTimeout, "the holder to renew the Lease", func() bool {
		current, err := s.cluster.lease()

		return (err == nil) && sameInstant(current.renewTime, renewedAt)
	})

	renewed := s.cluster.mustLease(t)
	if (renewed.holder != lease.holder) || (renewed.transitions != 1) {
		t.Fatalf("a renew changed the holder or the transition count: %q, %d", renewed.holder, renewed.transitions)
	}
}

// snapshotAsLeader checks the snapshot the holder queued on acquiring the Lease against what the cluster says.
func (s *scenario) snapshotAsLeader(t *testing.T) {
	eventually(t, stepTimeout, "the holder's first snapshot", func() bool { return s.holder.queue.count() >= 1 })

	record := s.holder.queue.records(t)[0]
	lease := s.cluster.mustLease(t)
	s.firstEpoch = lease

	if record.ReporterError != "" {
		t.Errorf("snapshot reporter_error = %q, want none", record.ReporterError)
	}

	kubeSystem := s.cluster.mustGet(t, "/api/v1/namespaces/kube-system")
	if want := stringAt(kubeSystem, "metadata", "uid"); record.KubeSystemUid != want {
		t.Errorf("snapshot kube_system_uid = %q, want %q", record.KubeSystemUid, want)
	}

	if strings.HasPrefix(record.ClusterVersion, "v1.33.") == false {
		t.Errorf("snapshot cluster_version = %q, want a v1.33 version", record.ClusterVersion)
	}

	if (record.RoleVersion != 1) || record.RoleOutdated {
		t.Errorf("snapshot role_version = %d, role_outdated = %v, want 1 and false", record.RoleVersion, record.RoleOutdated)
	}

	if record.CaFingerprint != s.cluster.caFingerprint {
		t.Errorf("snapshot ca_fingerprint = %q, want %q", record.CaFingerprint, s.cluster.caFingerprint)
	}

	if (record.LeaseUid != lease.uid) || (record.LeaseCreatedAt != lease.created) || (record.LeaseTransitions != 1) {
		t.Errorf("snapshot lease = (%q, %q, %d), want (%q, %q, 1)", record.LeaseUid, record.LeaseCreatedAt, record.LeaseTransitions, lease.uid, lease.created)
	}

	nodes := s.cluster.mustGet(t, "/api/v1/nodes")
	items, _ := nodes["items"].([]any)

	if len(items) != 1 {
		t.Fatalf("the k3s cluster lists %d nodes, want 1", len(items))
	}

	nodeName := stringAt(items[0].(map[string]any), "metadata", "name")

	if len(record.Nodes) != 1 {
		t.Fatalf("snapshot lists %d nodes, want the one k3s node %q", len(record.Nodes), nodeName)
	}

	node := record.Nodes[0]
	if node.Name != nodeName {
		t.Errorf("snapshot node = %q, want %q", node.Name, nodeName)
	}

	if node.ReadyStatus != "True" {
		t.Errorf("snapshot node ready_status = %q, want True", node.ReadyStatus)
	}

	if contains(node.Roles, "control-plane") == false {
		t.Errorf("snapshot node roles = %v, want control-plane among them", node.Roles)
	}

	if strings.HasPrefix(node.KubeletVersion, "v1.33.") == false {
		t.Errorf("snapshot node kubelet_version = %q, want a v1.33 version", node.KubeletVersion)
	}

	if node.PodCapacity <= 0 {
		t.Errorf("snapshot node pod_capacity = %d, want a positive capacity", node.PodCapacity)
	}

	t.Logf("snapshot: node %s ready=%s roles=%v pods=%d/%d, cluster %s", node.Name, node.ReadyStatus, node.Roles, node.PodCount, node.PodCapacity, record.ClusterVersion)
}

// failOver crashes the holder without a release and moves the lease clock. The standby must not take the Lease
// before a lease duration has passed and must take it afterwards, and the holder that wakes up must not write.
func (s *scenario) failOver(t *testing.T) {
	crashed, taker := s.holder, s.standby
	crashedIdentity := crashed.identity(t)

	// A crashed host stops dead: the loop is parked before it makes a request and stays there.
	crashed.freeze(t)

	// The standby sees the frozen holder's record at this moment; a lease duration is measured from here.
	taker.tick(t)
	taker.tick(t)

	if holder := s.cluster.mustLease(t).holder; holder != crashedIdentity {
		t.Fatalf("holder = %q after the crash, want it unchanged at %q", holder, crashedIdentity)
	}

	// Just short of the lease duration the standby still waits.
	s.clock.Advance(59 * time.Second)
	taker.tick(t)
	taker.tick(t)

	if holder := s.cluster.mustLease(t).holder; holder != crashedIdentity {
		t.Fatalf("the standby took the Lease after 59s, holder = %q", holder)
	}

	if got := taker.queue.count(); got != 0 {
		t.Fatalf("the standby queued %d snapshots before it held the Lease", got)
	}

	s.clock.Advance(2 * time.Second)
	taker.tick(t)

	takerIdentity := taker.identity(t)

	eventually(t, stepTimeout, "the standby to take the Lease", func() bool {
		lease, err := s.cluster.lease()

		return (err == nil) && (lease.holder == takerIdentity)
	})

	lease := s.cluster.mustLease(t)
	if lease.transitions != 2 {
		t.Errorf("leaseTransitions after the failover = %d, want 2", lease.transitions)
	}

	if lease.uid != s.firstEpoch.uid {
		t.Errorf("a failover changed the Lease uid from %q to %q", s.firstEpoch.uid, lease.uid)
	}

	eventually(t, stepTimeout, "the new holder's snapshot", func() bool { return taker.queue.count() >= 1 })

	if got := taker.queue.records(t)[0].LeaseTransitions; got != 2 {
		t.Errorf("the new holder's snapshot carries lease_transitions %d, want 2", got)
	}

	// The crashed holder wakes up believing it still leads. It must find the other holder and write nothing.
	lostBefore := crashed.lost.Load()
	snapshotsBefore := crashed.queue.count()

	crashed.unfreeze(t)
	crashed.tick(t)

	eventually(t, stepTimeout, "the old holder to notice it lost the Lease", func() bool { return crashed.lost.Load() > lostBefore })

	after := s.cluster.mustLease(t)
	if (after.holder != takerIdentity) || (after.transitions != 2) {
		t.Fatalf("the old holder disturbed the Lease: holder %q, transitions %d", after.holder, after.transitions)
	}

	if got := crashed.queue.count(); got != snapshotsBefore {
		t.Errorf("the old holder queued %d snapshots after it lost the Lease", got-snapshotsBefore)
	}

	s.holder, s.standby = taker, crashed
}

// releaseOnPause pauses the holder's gate. It must hand the Lease back, and the standby must take it at once.
func (s *scenario) releaseOnPause(t *testing.T) {
	paused, successor := s.holder, s.standby

	lostBefore := paused.lost.Load()

	paused.paused.Store(true)
	paused.tick(t)

	eventually(t, stepTimeout, "the paused holder to release the Lease", func() bool {
		lease, err := s.cluster.lease()

		return (err == nil) && (lease.holder == "")
	})

	eventually(t, stepTimeout, "the paused holder to stop leading", func() bool { return paused.lost.Load() > lostBefore })

	successorIdentity := successor.identity(t)

	successor.tick(t)

	eventually(t, stepTimeout, "the standby to take the released Lease", func() bool {
		lease, err := s.cluster.lease()

		return (err == nil) && (lease.holder == successorIdentity)
	})

	if got := s.cluster.mustLease(t).transitions; got != 3 {
		t.Errorf("leaseTransitions after the release and takeover = %d, want 3", got)
	}

	eventually(t, stepTimeout, "the new holder's snapshot", func() bool { return successor.queue.hasTransitions(t, 3) })

	paused.paused.Store(false)

	s.holder, s.standby = successor, paused
	s.lastTransitions = 3
}

// revokeToken deletes the holder's token Secret. The cluster must refuse the token with a 401, and the holder's
// elector must say so on its next Lease call, while a candidate with an intact token says nothing.
func (s *scenario) revokeToken(t *testing.T) {
	revoked, intact := s.holder, s.standby

	state := revoked.state(t)
	token := newReporterClient(t, state)

	if err := listOneNode(token); err != nil {
		t.Fatalf("the reporter token does not work before it is revoked: %v", err)
	}

	secretPath := secretsPath + "/" + tokenSecretPrefix + state.InstanceID
	s.cluster.deleteObject(t, secretPath)

	var refusal error

	deleted := time.Now()

	eventually(t, stepTimeout, "the cluster to refuse the deleted token", func() bool {
		refusal = listOneNode(token)

		return refusal != nil
	})

	lag := time.Since(deleted).Round(time.Second)

	var apiError *kube.APIError
	if errors.As(refusal, &apiError) == false {
		t.Fatalf("the refused token gave %v, want an API error", refusal)
	}

	if apiError.Code != http.StatusUnauthorized {
		t.Errorf("a token whose Secret was deleted gets HTTP %d, want 401", apiError.Code)
	}

	if kube.IsAuthRejected(refusal) == false {
		t.Errorf("IsAuthRejected = false for %v", refusal)
	}

	t.Logf("api fact: a deleted token Secret answers HTTP %d reason %q (%s), after the token had kept working for %s", apiError.Code, apiError.Reason, apiError.Message, lag)

	revoked.tick(t)

	eventually(t, stepTimeout, "the revoked candidate to report credentials_rejected", func() bool {
		return revoked.runtime.ReporterError() == "credentials_rejected"
	})

	if got := intact.runtime.ReporterError(); got != "" {
		t.Errorf("the candidate with an intact token reports %q, want none", got)
	}
}

// uninstallBoth runs uninstall on both hosts, as disabling reporting does. Nothing the agent installed may remain
// and the namespace must be gone or on its way.
func (s *scenario) uninstallBoth(t *testing.T) {
	for _, c := range []*candidate{s.a, s.b} {
		result := c.uninstall(t)
		if (result.Success == false) || (result.Message != "uninstalled") {
			t.Fatalf("%s: Uninstall = %+v", c.name, result)
		}

		if _, found, err := kubereporter.LoadState(c.dataDir); (err != nil) || found {
			t.Errorf("%s: the state file survived uninstall (found=%v, err=%v)", c.name, found, err)
		}
	}

	s.cluster.requireNothingInstalled(t)
}

// reEnrol waits for the namespace to disappear and enrols again. The new Lease is a new object, and the snapshot
// of the new holder must carry its epoch, with the transition count back at 1.
func (s *scenario) reEnrol(t *testing.T) {
	started := time.Now()

	eventually(t, namespaceGoneTimeout, "the vord-system namespace to be deleted", func() bool {
		_, err := s.cluster.admin.GetRaw(context.Background(), namespacePath)

		return kube.IsNotFound(err)
	})

	t.Logf("the namespace took %s to disappear", time.Since(started).Round(time.Second))

	oldIDs := []string{s.a.instanceIDOrEmpty(), s.b.instanceIDOrEmpty()}
	enrolAll(t, s.a, s.b)

	for _, c := range []*candidate{s.a, s.b} {
		if id := c.instanceID(t); contains(oldIDs, id) {
			t.Errorf("%s: re-enrolled with the old instance id %q", c.name, id)
		}
	}

	s.cluster.requireInstalled(t, s.a.instanceID(t), s.b.instanceID(t))

	// The loops pick the new state files up on their next tick.
	s.a.tick(t)
	s.b.tick(t)

	eventually(t, stepTimeout, "a candidate to take the new Lease", func() bool {
		lease, err := s.cluster.lease()

		return (err == nil) && (lease.holder != "")
	})

	lease := s.cluster.mustLease(t)
	s.secondEpoch = lease
	s.holder, s.standby = s.candidatesByHolder(t, lease.holder)

	if lease.uid == s.firstEpoch.uid {
		t.Errorf("the new Lease has the old uid %q", lease.uid)
	}

	if newer, older := parseTime(t, lease.created), parseTime(t, s.firstEpoch.created); newer.After(older) == false {
		t.Errorf("the new Lease was created at %s, not after the old one at %s", lease.created, s.firstEpoch.created)
	}

	if lease.transitions != 1 {
		t.Errorf("leaseTransitions of the new Lease = %d, want 1 (the old one ended at %d)", lease.transitions, s.lastTransitions)
	}

	eventually(t, stepTimeout, "a snapshot of the new Lease epoch", func() bool { return s.holder.queue.hasLeaseUID(t, lease.uid) })

	var record *pb.KubernetesClusterRecord

	for _, candidate := range s.holder.queue.records(t) {
		if candidate.LeaseUid == lease.uid {
			record = candidate
		}
	}

	if (record.LeaseCreatedAt != lease.created) || (record.LeaseTransitions != 1) {
		t.Errorf("snapshot lease = (%q, %d), want (%q, 1)", record.LeaseCreatedAt, record.LeaseTransitions, lease.created)
	}

	if len(record.Nodes) != 1 {
		t.Errorf("the new snapshot lists %d nodes, want 1", len(record.Nodes))
	}

	t.Logf("new epoch: uid %s created %s (old uid %s created %s), transitions %d -> %d", lease.uid, lease.created, s.firstEpoch.uid, s.firstEpoch.created, s.lastTransitions, record.LeaseTransitions)
}

// staleLeaseWrite replaces the Lease with the reporter's token from a stale resourceVersion, which is what the
// election's compare-and-swap relies on the cluster to refuse.
func (s *scenario) staleLeaseWrite(t *testing.T) {
	ctx := context.Background()
	client := newReporterClient(t, s.holder.state(t))

	original, err := client.GetRaw(ctx, leasePath)
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	fresh := deepCopy(t, original)
	annotate(fresh, "vord.framlux.io/test-touch", "fresh")

	if _, err := client.PutRaw(ctx, leasePath, fresh); err != nil {
		t.Fatalf("write the Lease from its current resourceVersion: %v", err)
	}

	stale := deepCopy(t, original)
	annotate(stale, "vord.framlux.io/test-touch", "stale")

	_, err = client.PutRaw(ctx, leasePath, stale)
	if err == nil {
		t.Fatal("the cluster accepted a write from a stale resourceVersion")
	}

	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		t.Fatalf("the stale write gave %v, want an API error", err)
	}

	if (apiError.Code != http.StatusConflict) || (apiError.Reason != "Conflict") {
		t.Errorf("a stale Lease write answers %d %q, want 409 Conflict", apiError.Code, apiError.Reason)
	}

	if (kube.IsConflict(err) == false) || kube.IsAlreadyExists(err) || kube.IsAuthRejected(err) || kube.IsNotFound(err) {
		t.Errorf("classification of the stale write: conflict=%v already_exists=%v auth_rejected=%v not_found=%v",
			kube.IsConflict(err), kube.IsAlreadyExists(err), kube.IsAuthRejected(err), kube.IsNotFound(err))
	}

	t.Logf("api fact: a stale-resourceVersion Lease PUT answers HTTP %d reason %q (%s)", apiError.Code, apiError.Reason, apiError.Message)
}

// deletePreconditions deletes objects of the kinds uninstall deletes with a uid precondition: the right uid
// deletes, a wrong or stale one is a Conflict that leaves the object alone.
func (s *scenario) deletePreconditions(t *testing.T) {
	cases := []struct {
		kind       string
		collection string
		manifest   func() map[string]any
	}{
		{
			kind:       "Namespace",
			collection: namespacesPath,
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "v1", "kind": "Namespace", "metadata": map[string]any{"name": "vord-it-precondition"}}
			},
		},
		{
			kind:       "Secret",
			collection: "/api/v1/namespaces/default/secrets",
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "v1", "kind": "Secret", "metadata": map[string]any{"name": "vord-it-precondition"}, "type": "Opaque"}
			},
		},
		{
			kind:       "Lease",
			collection: "/apis/coordination.k8s.io/v1/namespaces/default/leases",
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "coordination.k8s.io/v1", "kind": "Lease", "metadata": map[string]any{"name": "vord-it-precondition"}, "spec": map[string]any{}}
			},
		},
		{
			kind:       "ClusterRole",
			collection: clusterRolesPath,
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRole", "metadata": map[string]any{"name": "vord-it-precondition"}, "rules": []any{}}
			},
		},
	}

	for _, c := range cases {
		t.Run(c.kind, func(t *testing.T) {
			s.cluster.exercisePrecondition(t, c.kind, c.collection, c.manifest)
		})
	}
}

// missingLease deletes the Lease while the reporter's account and roles still exist. The reporter must get a 404
// for reading the missing Lease, a Conflict for writing it back at the resourceVersion it read, and a 403 only
// for a Lease it has no rule for, and the holder's elector must stop leading without calling that a credential
// problem.
func (s *scenario) missingLease(t *testing.T) {
	ctx := context.Background()
	holder, standby := s.holder, s.standby
	client := newReporterClient(t, holder.state(t))

	current, err := client.GetRaw(ctx, leasePath)
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	s.cluster.deleteObject(t, leasePath)

	anonymous := deepCopy(t, current)
	delete(anonymous["metadata"].(map[string]any), "resourceVersion")
	delete(anonymous["metadata"].(map[string]any), "uid")

	_, getErr := client.GetRaw(ctx, leasePath)
	_, putErr := client.PutRaw(ctx, leasePath, current)
	_, bareErr := client.PutRaw(ctx, leasePath, anonymous)
	_, otherErr := client.GetRaw(ctx, leasesPath+"/some-other-lease")
	_, listErr := listWith(ctx, client, leasesPath)

	expectStatus(t, "GET of the missing Lease", getErr, http.StatusNotFound, "NotFound")
	expectStatus(t, "PUT of the missing Lease with the resourceVersion it was read at", putErr, http.StatusConflict, "Conflict")
	expectStatus(t, "GET of another Lease name", otherErr, http.StatusForbidden, "Forbidden")
	expectStatus(t, "LIST of Leases", listErr, http.StatusForbidden, "Forbidden")

	if kube.IsAuthRejected(getErr) || (kube.IsNotFound(getErr) == false) {
		t.Errorf("classification of the missing Lease: auth_rejected=%v not_found=%v", kube.IsAuthRejected(getErr), kube.IsNotFound(getErr))
	}

	if kube.IsAuthRejected(otherErr) == false {
		t.Error("a 403 for a Lease the reporter has no rule for is not classified as rejected credentials")
	}

	t.Logf("api fact: for a missing Lease the reporter's GET answers %s, a PUT of the object it read (resourceVersion and uid) answers %s and a PUT with neither answers %s; a Lease name its Role does not name answers %s",
		statusOf(getErr), statusOf(putErr), statusOf(bareErr), statusOf(otherErr))

	lostBefore := holder.lost.Load()

	holder.tick(t)

	eventually(t, stepTimeout, "the holder to stop leading once the Lease is gone", func() bool { return holder.lost.Load() > lostBefore })

	standby.tick(t)
	standby.tick(t)

	for _, c := range []*candidate{holder, standby} {
		if got := c.runtime.ReporterError(); got != "" {
			t.Errorf("%s reports %q for a missing Lease, which is no credential problem", c.name, got)
		}
	}
}

// terminatingNamespace keeps the namespace in Terminating with a labelled object that holds a finalizer, then
// checks what the real API says to deletes and creates in it and how uninstall and enrol take it.
func (s *scenario) terminatingNamespace(t *testing.T) {
	ctx := context.Background()
	admin := s.cluster.admin

	hold := map[string]any{
		"apiVersion": "v1",
		"kind":       "ConfigMap",
		"metadata": map[string]any{
			"name":       "vord-it-hold",
			"namespace":  reporterNamespace,
			"labels":     map[string]any{managedByLabel: managedByValue},
			"finalizers": []any{"vord.framlux.io/test-hold"},
		},
	}

	if err := admin.Do(ctx, http.MethodPost, configMapsPath, nil, hold, nil); err != nil {
		t.Fatalf("create the object that holds the namespace: %v", err)
	}

	result := s.a.uninstall(t)
	if (result.Success == false) || (result.Message != "uninstalled") {
		t.Fatalf("uninstall with a held object: %+v", result)
	}

	namespace := s.cluster.mustGet(t, namespacePath)
	if stringAt(namespace, "metadata", "deletionTimestamp") == "" {
		t.Fatalf("the namespace is not terminating after uninstall: %v", namespace["status"])
	}

	uid := stringAt(namespace, "metadata", "uid")

	// A delete of a namespace that is already terminating, with the uid precondition uninstall sends.
	status, body := s.cluster.raw(t, http.MethodDelete, namespacePath, deleteOptions(uid))
	if status != http.StatusOK {
		t.Errorf("DELETE of a terminating namespace answers %d, want 200: %s", status, body)
	}

	t.Logf("api fact: DELETE of an already-terminating namespace with a uid precondition answers HTTP %d (kind %v, deletionTimestamp %v)",
		status, body["kind"], nestedValue(body, "metadata", "deletionTimestamp"))

	plainStatus, _ := s.cluster.raw(t, http.MethodDelete, namespacePath, nil)
	staleStatus, staleBody := s.cluster.raw(t, http.MethodDelete, namespacePath, deleteOptions("00000000-0000-0000-0000-000000000000"))

	t.Logf("api fact: DELETE of a terminating namespace without a body answers HTTP %d; with a wrong uid HTTP %d %v", plainStatus, staleStatus, staleBody["reason"])

	// Uninstall run again on every host while the namespace is terminating must still finish.
	for _, c := range []*candidate{s.b, s.a} {
		again := c.uninstall(t)
		if (again.Success == false) || (again.Message != "uninstalled") {
			t.Errorf("%s: uninstall on a terminating namespace = %+v", c.name, again)
		}
	}

	if stringAt(s.cluster.mustGet(t, namespacePath), "metadata", "deletionTimestamp") == "" {
		t.Error("the held namespace is no longer terminating")
	}

	// A create in the terminating namespace is refused with a 403 whose cause says why.
	secret := map[string]any{"apiVersion": "v1", "kind": "Secret", "metadata": map[string]any{"name": "vord-it-created"}, "type": "Opaque"}
	createErr := admin.Do(ctx, http.MethodPost, secretsPath, nil, secret, nil)

	var apiError *kube.APIError
	if errors.As(createErr, &apiError) == false {
		t.Fatalf("a create in a terminating namespace gave %v, want an API error", createErr)
	}

	causes := make([]string, 0, len(apiError.Causes))
	for _, cause := range apiError.Causes {
		causes = append(causes, cause.Type)
	}

	if (apiError.Code != http.StatusForbidden) || (apiError.Reason != "Forbidden") || contains(causes, "NamespaceTerminating") == false {
		t.Errorf("a create in a terminating namespace answers %d %q causes %v, want 403 Forbidden with NamespaceTerminating", apiError.Code, apiError.Reason, causes)
	}

	if (kube.IsNamespaceTerminating(createErr) == false) || kube.IsAuthRejected(createErr) {
		t.Errorf("classification of the create: namespace_terminating=%v auth_rejected=%v", kube.IsNamespaceTerminating(createErr), kube.IsAuthRejected(createErr))
	}

	t.Logf("api fact: a POST into a terminating namespace answers HTTP %d reason %q causes %v (%s)", apiError.Code, apiError.Reason, causes, apiError.Message)

	// Enrol meets the terminating namespace and waits for it on a clock of its own, so no real minute passes.
	patient := newFakeClock(time.Now().UTC())
	enrolDir := t.TempDir()
	enrolling := kubereporter.New(kubereporter.Options{DataDir: enrolDir, Facts: s.cluster.factsSource(), Now: patient.Now, Sleep: patient.Sleep})

	enrolCtx, cancel := context.WithTimeout(ctx, commandTimeout)
	defer cancel()

	if enrolled := enrolling.Enrol(enrolCtx); (enrolled.Success) || (enrolled.Message != "namespace_terminating") {
		t.Errorf("enrol into a terminating namespace = %+v, want namespace_terminating", enrolled)
	}

	if _, found, _ := kubereporter.LoadState(enrolDir); found {
		t.Error("enrol into a terminating namespace left a state file")
	}

	// Letting the object go lets the namespace finish.
	held := s.cluster.mustGet(t, configMapsPath+"/vord-it-hold")
	delete(held["metadata"].(map[string]any), "finalizers")

	if _, err := admin.PutRaw(ctx, configMapsPath+"/vord-it-hold", held); err != nil {
		t.Fatalf("release the object that holds the namespace: %v", err)
	}

	eventually(t, namespaceGoneTimeout, "the held namespace to disappear", func() bool {
		_, err := admin.GetRaw(ctx, namespacePath)

		return kube.IsNotFound(err)
	})
}

// k3sCluster is the k3s container and the credentials to reach it.
type k3sCluster struct {
	kubeconfig    string
	config        kube.Config
	admin         *kube.Client
	caFingerprint string
	rawClient     *http.Client
}

// startK3s starts k3s in a Podman container, waits for its admin kubeconfig and for the node to be Ready, and
// copies the kubeconfig out with its server pointed at the published port. The container is removed when the test
// ends. Without podman the test skips.
func startK3s(t *testing.T) *k3sCluster {
	t.Helper()

	podman, err := exec.LookPath("podman")
	if err != nil {
		t.Skip("podman is not on PATH; this test starts k3s in a Podman container, so it cannot run here")
	}

	name := "vord-k3s-it-" + randomHex(t, 4)

	t.Cleanup(func() {
		if t.Failed() {
			logs, _ := exec.Command(podman, "logs", "--tail", "40", name).CombinedOutput()
			t.Logf("the last lines of the k3s log:\n%s", logs)
		}

		if out, err := exec.Command(podman, "rm", "--force", "--volumes", "--ignore", name).CombinedOutput(); err != nil {
			t.Logf("could not remove the k3s container %s: %v\n%s", name, err, out)
		}
	})

	runPodman(t, podman, "run", "--privileged", "--detach", "--rm",
		"--name", name,
		"--label", "vord-test=k3s-integration",
		"--publish", "127.0.0.1::6443",
		k3sImage, "server", "--disable=traefik", "--disable=servicelb", "--disable=metrics-server")

	mapping := strings.SplitN(runPodman(t, podman, "port", name, "6443/tcp"), "\n", 2)[0]

	_, port, err := net.SplitHostPort(strings.TrimSpace(mapping))
	if err != nil {
		t.Fatalf("podman port gave %q: %v", mapping, err)
	}

	eventually(t, 2*time.Minute, "k3s to write its admin kubeconfig", func() bool {
		return exec.Command(podman, "exec", name, "test", "-s", k3sKubeconfigFile).Run() == nil
	})

	local := filepath.Join(t.TempDir(), "k3s.yaml")
	runPodman(t, podman, "cp", name+":"+k3sKubeconfigFile, local)

	original, err := os.ReadFile(local)
	if err != nil {
		t.Fatalf("read the copied kubeconfig: %v", err)
	}

	rewritten := serverLine.ReplaceAll(original, []byte("${1}https://127.0.0.1:"+port))
	if bytes.Contains(rewritten, []byte("https://127.0.0.1:"+port)) == false {
		t.Fatalf("the copied kubeconfig has no server entry to point at port %s", port)
	}

	if err := os.WriteFile(local, rewritten, 0o600); err != nil {
		t.Fatalf("write the kubeconfig: %v", err)
	}

	config, err := kube.LoadKubeconfig(local, nil)
	if err != nil {
		t.Fatalf("load the kubeconfig: %v", err)
	}

	admin, err := kube.New(config)
	if err != nil {
		t.Fatalf("build the admin client: %v", err)
	}

	fingerprint, err := kubelocal.CAFingerprint(config.CAData)
	if err != nil {
		t.Fatalf("fingerprint of the cluster CA: %v", err)
	}

	rawClient, err := newRawClient(config)
	if err != nil {
		t.Fatalf("build the raw client: %v", err)
	}

	eventually(t, 3*time.Minute, "the k3s node to be Ready", func() bool {
		nodes, err := admin.GetRaw(context.Background(), "/api/v1/nodes")

		return (err == nil) && anyNodeReady(nodes)
	})

	return &k3sCluster{kubeconfig: local, config: config, admin: admin, caFingerprint: fingerprint, rawClient: rawClient}
}

// newRawClient builds an HTTP client that authenticates like the admin kubeconfig, for the calls whose exact
// status code is the question and not just whether they failed.
func newRawClient(config kube.Config) (*http.Client, error) {
	roots := x509.NewCertPool()
	if roots.AppendCertsFromPEM(config.CAData) == false {
		return nil, errors.New("the cluster CA holds no certificate")
	}

	pair, err := tls.X509KeyPair(config.ClientCert, config.ClientKey)
	if err != nil {
		return nil, err
	}

	return &http.Client{
		Timeout: 30 * time.Second,
		Transport: &http.Transport{
			TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, RootCAs: roots, Certificates: []tls.Certificate{pair}},
		},
	}, nil
}

// factsSource says this host is a control plane node with an admin kubeconfig at the copied path, which is what
// lets the reporter commands run on a machine that is not a k3s server.
func (k *k3sCluster) factsSource() kubereporter.FactsSource {
	facts := kubelocal.Facts{
		Distro:                 "k3s",
		NodeName:               "k3s-integration",
		NodeNameSource:         "hostname",
		CaFingerprint:          k.caFingerprint,
		HasKubelet:             true,
		ControlPlane:           true,
		AdminCredentialPresent: true,
		AdminKubeconfigPath:    k.kubeconfig,
	}

	return func(context.Context) (kubelocal.Facts, bool) { return facts, true }
}

// raw sends one request as the cluster admin and returns the HTTP status and the JSON object that came back.
func (k *k3sCluster) raw(t *testing.T, method string, path string, body any) (int, map[string]any) {
	t.Helper()

	var payload io.Reader

	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("encode the %s %s body: %v", method, path, err)
		}

		payload = bytes.NewReader(encoded)
	}

	request, err := http.NewRequest(method, k.config.Server+path, payload)
	if err != nil {
		t.Fatalf("build %s %s: %v", method, path, err)
	}

	request.Header.Set("Accept", "application/json")

	if body != nil {
		request.Header.Set("Content-Type", "application/json")
	}

	response, err := k.rawClient.Do(request)
	if err != nil {
		t.Fatalf("%s %s: %v", method, path, err)
	}

	defer func() { _ = response.Body.Close() }()

	data, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read the %s %s response: %v", method, path, err)
	}

	decoded := map[string]any{}
	_ = json.Unmarshal(data, &decoded)

	return response.StatusCode, decoded
}

func (k *k3sCluster) mustGet(t *testing.T, path string) map[string]any {
	t.Helper()

	obj, err := k.admin.GetRaw(context.Background(), path)
	if err != nil {
		t.Fatalf("GET %s: %v", path, err)
	}

	return obj
}

// deleteObject deletes the object at path with the uid precondition uninstall uses.
func (k *k3sCluster) deleteObject(t *testing.T, path string) {
	t.Helper()

	uid := stringAt(k.mustGet(t, path), "metadata", "uid")

	if err := k.admin.Do(context.Background(), http.MethodDelete, path, nil, deleteOptions(uid), nil); err != nil {
		t.Fatalf("DELETE %s: %v", path, err)
	}
}

// leaseView is the part of the reporter Lease the test looks at.
type leaseView struct {
	holder      string
	transitions int64
	renewTime   string
	uid         string
	created     string
	roleVersion string
}

func parseLease(obj map[string]any) leaseView {
	view := leaseView{
		holder:      stringAt(obj, "spec", "holderIdentity"),
		renewTime:   stringAt(obj, "spec", "renewTime"),
		uid:         stringAt(obj, "metadata", "uid"),
		created:     stringAt(obj, "metadata", "creationTimestamp"),
		roleVersion: stringAt(obj, "metadata", "annotations", roleVersionAnnotation),
	}

	if spec, ok := obj["spec"].(map[string]any); ok {
		if number, ok := spec["leaseTransitions"].(json.Number); ok {
			view.transitions, _ = number.Int64()
		}
	}

	return view
}

// lease reads the reporter Lease as the admin.
func (k *k3sCluster) lease() (leaseView, error) {
	obj, err := k.admin.GetRaw(context.Background(), leasePath)
	if err != nil {
		return leaseView{}, err
	}

	return parseLease(obj), nil
}

func (k *k3sCluster) mustLease(t *testing.T) leaseView {
	t.Helper()

	lease, err := k.lease()
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	return lease
}

// names lists the names of the objects in a collection, optionally only the ones with the Vord label. A
// collection in a namespace that does not exist lists nothing.
func (k *k3sCluster) names(path string, onlyManaged bool) ([]string, error) {
	var query url.Values
	if onlyManaged {
		query = url.Values{"labelSelector": {managedByLabel + "=" + managedByValue}}
	}

	var names []string

	err := k.admin.List(context.Background(), path, query, 100, func(body []byte) error {
		var page struct {
			Items []struct {
				Metadata struct {
					Name string `json:"name"`
				} `json:"metadata"`
			} `json:"items"`
		}

		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		for _, item := range page.Items {
			names = append(names, item.Metadata.Name)
		}

		return nil
	}, func() { names = nil })
	if err != nil {
		if kube.IsNotFound(err) {
			return nil, nil
		}

		return nil, err
	}

	return names, nil
}

// requireInstalled checks that the cluster holds exactly one of every shared object and one token Secret for each
// of the given instance ids, all with the Vord label, and that the roles are at the compiled version.
func (k *k3sCluster) requireInstalled(t *testing.T, instanceIDs ...string) {
	t.Helper()

	for _, path := range []string{namespacePath, serviceAccountPath, clusterRolePath, clusterRoleBindingPath, rolePath, roleBindingPath, leasePath} {
		obj := k.mustGet(t, path)

		if stringAt(obj, "metadata", "labels", managedByLabel) != managedByValue {
			t.Errorf("%s lacks the %s label", path, managedByLabel)
		}
	}

	for _, collection := range []struct {
		path string
		want int
	}{
		{namespacesPath, 1},
		{serviceAccountsPath, 1},
		{clusterRolesPath, 1},
		{clusterRoleBindingsPath, 1},
		{rolesPath, 1},
		{roleBindingsPath, 1},
		{leasesPath, 1},
		{secretsPath, len(instanceIDs)},
	} {
		names, err := k.names(collection.path, true)
		if err != nil {
			t.Fatalf("list %s: %v", collection.path, err)
		}

		if len(names) != collection.want {
			t.Errorf("%s holds %d labelled objects %v, want %d", collection.path, len(names), names, collection.want)
		}
	}

	for _, id := range instanceIDs {
		secret := k.mustGet(t, secretsPath+"/"+tokenSecretPrefix+id)

		if stringAt(secret, "type") != "kubernetes.io/service-account-token" {
			t.Errorf("the token Secret of %s has type %q", id, stringAt(secret, "type"))
		}
	}

	if got := k.mustLease(t).roleVersion; got != "1" {
		t.Errorf("the Lease records role version %q, want 1", got)
	}
}

// requireNothingInstalled checks that nothing the agent installed is left: the named objects are gone, nothing in
// vord-system carries the Vord label or is anything but what the cluster itself puts in a namespace, and the
// namespace is gone or terminating.
func (k *k3sCluster) requireNothingInstalled(t *testing.T) {
	t.Helper()

	for _, path := range []string{serviceAccountPath, clusterRolePath, clusterRoleBindingPath, rolePath, roleBindingPath, leasePath} {
		if _, err := k.admin.GetRaw(context.Background(), path); kube.IsNotFound(err) == false {
			t.Errorf("GET %s after uninstall = %v, want not found", path, err)
		}
	}

	for _, collection := range []string{secretsPath, configMapsPath, serviceAccountsPath, rolesPath, roleBindingsPath, leasesPath} {
		names, err := k.names(collection, false)
		if err != nil {
			t.Fatalf("list %s: %v", collection, err)
		}

		for _, name := range names {
			if (name != "default") && (name != "kube-root-ca.crt") {
				t.Errorf("%s still holds %q after uninstall", collection, name)
			}
		}
	}

	for _, collection := range []string{namespacesPath, clusterRolesPath, clusterRoleBindingsPath} {
		names, err := k.names(collection, true)
		if err != nil {
			t.Fatalf("list %s: %v", collection, err)
		}

		for _, name := range names {
			if (collection == namespacesPath) && (name == reporterNamespace) {
				continue
			}

			t.Errorf("%s still holds the labelled %q after uninstall", collection, name)
		}
	}

	namespace, err := k.admin.GetRaw(context.Background(), namespacePath)

	switch {
	case kube.IsNotFound(err):
		t.Log("after uninstall the vord-system namespace is already gone")
	case err != nil:
		t.Fatalf("GET the namespace after uninstall: %v", err)
	case stringAt(namespace, "metadata", "deletionTimestamp") == "":
		t.Errorf("after uninstall the vord-system namespace is neither gone nor terminating (phase %q)", stringAt(namespace, "status", "phase"))
	default:
		t.Log("after uninstall the vord-system namespace is terminating")
	}
}

// exercisePrecondition runs the delete-by-uid cases on one kind of object.
func (k *k3sCluster) exercisePrecondition(t *testing.T, kind string, collection string, manifest func() map[string]any) {
	t.Helper()

	ctx := context.Background()
	name := stringAt(manifest(), "metadata", "name")
	path := collection + "/" + name

	create := func() string {
		var created map[string]any

		if err := k.admin.Do(ctx, http.MethodPost, collection, nil, manifest(), &created); err != nil {
			t.Fatalf("create the %s: %v", kind, err)
		}

		return stringAt(created, "metadata", "uid")
	}

	gone := func() {
		eventually(t, namespaceGoneTimeout, "the "+kind+" to be gone", func() bool {
			_, err := k.admin.GetRaw(ctx, path)

			return kube.IsNotFound(err)
		})
	}

	refused := func(uid string, label string) {
		err := k.admin.Do(ctx, http.MethodDelete, path, nil, deleteOptions(uid), nil)

		var apiError *kube.APIError
		if errors.As(err, &apiError) == false {
			t.Fatalf("%s: a delete with %s gave %v, want a Conflict", kind, label, err)
		}

		if (apiError.Code != http.StatusConflict) || (apiError.Reason != "Conflict") || (kube.IsConflict(err) == false) {
			t.Errorf("%s: a delete with %s answers %d %q, want 409 Conflict", kind, label, apiError.Code, apiError.Reason)
		}

		t.Logf("api fact: DELETE of a %s with %s answers HTTP %d reason %q (%s)", kind, label, apiError.Code, apiError.Reason, apiError.Message)
	}

	first := create()

	refused("00000000-0000-0000-0000-000000000000", "a wrong uid")

	if still := stringAt(k.mustGet(t, path), "metadata", "uid"); still != first {
		t.Fatalf("%s: the object changed after a refused delete: uid %q, want %q", kind, still, first)
	}

	status, _ := k.raw(t, http.MethodDelete, path, deleteOptions(first))
	if status != http.StatusOK {
		t.Errorf("%s: a delete with the right uid answers %d, want 200", kind, status)
	}

	t.Logf("api fact: DELETE of a %s with the right uid answers HTTP %d", kind, status)

	gone()

	second := create()
	if second == first {
		t.Fatalf("%s: the replacement has the old uid %q", kind, first)
	}

	refused(first, "the uid of the object it replaced")

	if still := stringAt(k.mustGet(t, path), "metadata", "uid"); still != second {
		t.Fatalf("%s: the replacement was deleted by the old uid", kind)
	}

	if err := k.admin.Do(ctx, http.MethodDelete, path, nil, deleteOptions(second), nil); err != nil {
		t.Fatalf("%s: delete the replacement: %v", kind, err)
	}

	gone()
}

// candidate is one host: a reporter for its commands and a runtime for its election, each with its own data
// directory.
type candidate struct {
	name     string
	dataDir  string
	reporter *kubereporter.Reporter
	runtime  *kubereporter.Runtime
	queue    *snapshotQueue
	ticks    chan time.Time

	paused atomic.Bool
	lost   atomic.Int32

	// parkNext, parked, thaw and release freeze the loop at the start of its next election step.
	parkNext atomic.Bool
	parked   chan struct{}
	thaw     chan struct{}
	release  chan struct{}
	releaser sync.Once

	started  bool
	finished chan struct{}
}

func newCandidate(t *testing.T, name string, cluster *k3sCluster, clock *fakeClock) *candidate {
	t.Helper()

	c := &candidate{
		name:     name,
		dataDir:  t.TempDir(),
		queue:    &snapshotQueue{},
		ticks:    make(chan time.Time),
		parked:   make(chan struct{}, 1),
		thaw:     make(chan struct{}),
		release:  make(chan struct{}),
		finished: make(chan struct{}),
	}

	facts := cluster.factsSource()

	c.runtime = kubereporter.NewRuntime(kubereporter.RuntimeOptions{
		DataDir: c.dataDir,
		Clock:   clock,
		Gate: kubereporter.Gate{
			Paused:    c.gatePaused,
			LastAck:   clock.Now,
			StartedAt: clock.Now(),
		},
		OnLost:        func() { c.lost.Add(1) },
		Snapshots:     c.queue,
		Facts:         facts,
		Ticks:         c.ticks,
		SnapshotTicks: make(chan time.Time),
	})

	c.reporter = kubereporter.New(kubereporter.Options{DataDir: c.dataDir, Facts: facts, Releaser: c.runtime})

	return c
}

// gatePaused is the gate's answer to whether the host is paused. It is also where a frozen loop stops: the first
// thing an election step does after reading the clock.
func (c *candidate) gatePaused() bool {
	if c.parkNext.CompareAndSwap(true, false) {
		c.parked <- struct{}{}

		select {
		case <-c.thaw:
		case <-c.release:
		}
	}

	return c.paused.Load()
}

// start runs the loop once ready is closed, so that several candidates begin their first step together.
func (c *candidate) start(ctx context.Context, ready <-chan struct{}) {
	c.started = true

	go func() {
		defer close(c.finished)

		<-ready
		c.runtime.Run(ctx)
	}()
}

// stop lets a frozen loop go and waits for the loop to finish after its context ended.
func (c *candidate) stop(t *testing.T) {
	t.Helper()

	c.releaser.Do(func() { close(c.release) })

	if c.started == false {
		return
	}

	select {
	case <-c.finished:
	case <-time.After(stepTimeout):
		t.Errorf("%s: the reporter loop did not stop", c.name)
	}
}

// tick gives the loop one election step. The loop takes a tick only between steps, so a tick that is taken means
// the step before it has finished.
func (c *candidate) tick(t *testing.T) {
	t.Helper()

	select {
	case c.ticks <- time.Time{}:
	case <-time.After(stepTimeout):
		t.Fatalf("%s: the reporter loop took no tick within %s", c.name, stepTimeout)
	}
}

// freeze stops the loop dead, as a crashed host would be: it takes a tick and parks at the start of that step,
// before it makes any request, and stays there until unfreeze.
func (c *candidate) freeze(t *testing.T) {
	t.Helper()

	c.parkNext.Store(true)
	c.tick(t)

	select {
	case <-c.parked:
	case <-time.After(stepTimeout):
		t.Fatalf("%s: the reporter loop did not stop at the start of its step", c.name)
	}
}

// unfreeze lets a frozen loop carry on with the step it was parked in.
func (c *candidate) unfreeze(t *testing.T) {
	t.Helper()

	select {
	case c.thaw <- struct{}{}:
	case <-time.After(stepTimeout):
		t.Fatalf("%s: the frozen loop did not wake up", c.name)
	}
}

func (c *candidate) state(t *testing.T) kubereporter.State {
	t.Helper()

	state, found, err := kubereporter.LoadState(c.dataDir)
	if (err != nil) || (found == false) {
		t.Fatalf("%s: the state file is not usable (found=%v, err=%v)", c.name, found, err)
	}

	return state
}

func (c *candidate) instanceID(t *testing.T) string {
	t.Helper()

	return c.state(t).InstanceID
}

// instanceIDOrEmpty is the instance id the host enrolled with, or an empty string when it has no state file.
func (c *candidate) instanceIDOrEmpty() string {
	state, found, err := kubereporter.LoadState(c.dataDir)
	if (err != nil) || (found == false) {
		return ""
	}

	return state.InstanceID
}

// identity is the name the candidate holds the Lease under.
func (c *candidate) identity(t *testing.T) string {
	t.Helper()

	return "vord-" + c.instanceID(t)
}

func (c *candidate) uninstall(t *testing.T) commands.Result {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()

	return c.reporter.Uninstall(ctx)
}

// candidatesByHolder returns the candidate that holds the Lease under the given identity, and the other one.
func (s *scenario) candidatesByHolder(t *testing.T, holder string) (*candidate, *candidate) {
	t.Helper()

	switch holder {
	case s.a.identity(t):
		return s.a, s.b
	case s.b.identity(t):
		return s.b, s.a
	}

	t.Fatalf("the Lease is held by %q, which is neither candidate (%s, %s)", holder, s.a.identity(t), s.b.identity(t))

	return nil, nil
}

// enrolAll enrols the candidates at the same moment and requires every one to succeed.
func enrolAll(t *testing.T, candidates ...*candidate) {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()

	results := make([]commands.Result, len(candidates))

	var wg sync.WaitGroup

	for i := range candidates {
		wg.Add(1)

		go func() {
			defer wg.Done()

			results[i] = candidates[i].reporter.Enrol(ctx)
		}()
	}

	wg.Wait()

	for i, result := range results {
		if (result.Success == false) || (result.Message != "enrolled") {
			t.Fatalf("%s: Enrol = %+v", candidates[i].name, result)
		}
	}
}

// snapshotQueue stands in for the agent's telemetry queue and keeps every cluster snapshot it is given.
type snapshotQueue struct {
	mu       sync.Mutex
	payloads []string
}

func (q *snapshotQueue) ReplaceTelemetryOfType(_ string, kind db.TelemetryType, payload string) error {
	if kind != db.TelemetryKubernetesCluster {
		return fmt.Errorf("unexpected telemetry type %d", kind)
	}

	q.mu.Lock()
	defer q.mu.Unlock()

	q.payloads = append(q.payloads, payload)

	return nil
}

func (q *snapshotQueue) count() int {
	q.mu.Lock()
	defer q.mu.Unlock()

	return len(q.payloads)
}

// records decodes every snapshot queued so far, oldest first.
func (q *snapshotQueue) records(t *testing.T) []*pb.KubernetesClusterRecord {
	t.Helper()

	q.mu.Lock()
	payloads := append([]string(nil), q.payloads...)
	q.mu.Unlock()

	records := make([]*pb.KubernetesClusterRecord, 0, len(payloads))

	for _, payload := range payloads {
		record := &pb.KubernetesClusterRecord{}
		if err := protojson.Unmarshal([]byte(payload), record); err != nil {
			t.Fatalf("decode a queued snapshot: %v", err)
		}

		records = append(records, record)
	}

	return records
}

func (q *snapshotQueue) hasTransitions(t *testing.T, transitions int64) bool {
	t.Helper()

	for _, record := range q.records(t) {
		if record.LeaseTransitions == transitions {
			return true
		}
	}

	return false
}

func (q *snapshotQueue) hasLeaseUID(t *testing.T, uid string) bool {
	t.Helper()

	for _, record := range q.records(t) {
		if record.LeaseUid == uid {
			return true
		}
	}

	return false
}

// fakeClock is the time the election runs on. Only the test moves it.
type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

func newFakeClock(start time.Time) *fakeClock {
	return &fakeClock{now: start}
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()

	return c.now
}

func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.now = c.now.Add(d)
}

// Sleep moves the clock by d without waiting, which is how a reporter command is made to run out its waits.
func (c *fakeClock) Sleep(ctx context.Context, d time.Duration) error {
	if err := ctx.Err(); err != nil {
		return err
	}

	c.Advance(d)

	return nil
}

// newReporterClient builds a client that authenticates with a candidate's reporter token, as its elector does.
func newReporterClient(t *testing.T, state kubereporter.State) *kube.Client {
	t.Helper()

	client, err := kube.New(kube.Config{Server: state.Server, CAData: []byte(state.CACert), TLSServerName: state.TLSServerName, Token: state.Token})
	if err != nil {
		t.Fatalf("build the reporter client: %v", err)
	}

	return client
}

// deleteOptions is the body of a DELETE that only deletes the object with the given uid.
func deleteOptions(uid string) map[string]any {
	return map[string]any{"apiVersion": "v1", "kind": "DeleteOptions", "preconditions": map[string]any{"uid": uid}}
}

// listOneNode lists a single node, which the reporter's ClusterRole allows and enrol uses to prove a token works.
func listOneNode(client *kube.Client) error {
	return client.Do(context.Background(), http.MethodGet, "/api/v1/nodes", url.Values{"limit": {"1"}}, nil, nil)
}

// listWith lists a collection once and returns how many items came back.
func listWith(ctx context.Context, client *kube.Client, path string) (int, error) {
	total := 0

	err := client.List(ctx, path, nil, 100, func(body []byte) error {
		var page struct {
			Items []json.RawMessage `json:"items"`
		}

		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		total += len(page.Items)

		return nil
	}, func() { total = 0 })

	return total, err
}

// expectStatus requires err to be an API error with the given status and reason.
func expectStatus(t *testing.T, what string, err error, code int, reason string) {
	t.Helper()

	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		t.Errorf("%s gave %v, want HTTP %d %s", what, err, code, reason)

		return
	}

	if (apiError.Code != code) || (apiError.Reason != reason) {
		t.Errorf("%s answers %d %q, want %d %q", what, apiError.Code, apiError.Reason, code, reason)
	}
}

// statusOf names the HTTP status and reason of an API error.
func statusOf(err error) string {
	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		return fmt.Sprintf("%v", err)
	}

	return fmt.Sprintf("HTTP %d %s", apiError.Code, apiError.Reason)
}

// eventually polls condition against the real cluster until it holds or the timeout passes.
func eventually(t *testing.T, timeout time.Duration, what string, condition func() bool) {
	t.Helper()

	deadline := time.Now().Add(timeout)

	for {
		if condition() {
			return
		}

		if time.Now().After(deadline) {
			t.Fatalf("gave up after %s waiting for %s", timeout, what)
		}

		time.Sleep(pollInterval)
	}
}

// runPodman runs a podman command and returns its trimmed output; a failure ends the test.
func runPodman(t *testing.T, podman string, args ...string) string {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	out, err := exec.CommandContext(ctx, podman, args...).CombinedOutput()
	if err != nil {
		t.Fatalf("podman %s: %v\n%s", strings.Join(args, " "), err, out)
	}

	return strings.TrimSpace(string(out))
}

func randomHex(t *testing.T, bytesLength int) string {
	t.Helper()

	buffer := make([]byte, bytesLength)
	if _, err := rand.Read(buffer); err != nil {
		t.Fatalf("random bytes: %v", err)
	}

	return hex.EncodeToString(buffer)
}

// anyNodeReady reports whether a node list holds a node whose Ready condition is True.
func anyNodeReady(nodes map[string]any) bool {
	items, _ := nodes["items"].([]any)

	for _, item := range items {
		node, _ := item.(map[string]any)
		conditions, _ := nestedValue(node, "status", "conditions").([]any)

		for _, entry := range conditions {
			condition, _ := entry.(map[string]any)

			if (condition["type"] == "Ready") && (condition["status"] == "True") {
				return true
			}
		}
	}

	return false
}

// nestedValue reads a value from nested generic JSON objects, and is nil when any step is missing.
func nestedValue(obj map[string]any, path ...string) any {
	var current any = obj

	for _, key := range path {
		next, ok := current.(map[string]any)
		if ok == false {
			return nil
		}

		current = next[key]
	}

	return current
}

// stringAt reads a string from nested generic JSON objects, and is "" when any step is missing.
func stringAt(obj map[string]any, path ...string) string {
	text, _ := nestedValue(obj, path...).(string)

	return text
}

// annotate sets one annotation on a raw object.
func annotate(obj map[string]any, key string, value string) {
	meta, _ := obj["metadata"].(map[string]any)

	annotations, ok := meta["annotations"].(map[string]any)
	if ok == false {
		annotations = map[string]any{}
		meta["annotations"] = annotations
	}

	annotations[key] = value
}

// deepCopy copies a raw object through JSON, keeping numbers exact.
func deepCopy(t *testing.T, obj map[string]any) map[string]any {
	t.Helper()

	data, err := json.Marshal(obj)
	if err != nil {
		t.Fatalf("copy an object: %v", err)
	}

	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()

	var copied map[string]any
	if err := decoder.Decode(&copied); err != nil {
		t.Fatalf("copy an object: %v", err)
	}

	return copied
}

func contains(list []string, want string) bool {
	for _, item := range list {
		if item == want {
			return true
		}
	}

	return false
}

func parseTime(t *testing.T, text string) time.Time {
	t.Helper()

	parsed, err := time.Parse(time.RFC3339Nano, text)
	if err != nil {
		t.Fatalf("parse the time %q: %v", text, err)
	}

	return parsed
}

// sameInstant reports whether the API's rendering of a MicroTime is the given instant.
func sameInstant(text string, instant time.Time) bool {
	parsed, err := time.Parse(time.RFC3339Nano, text)

	return (err == nil) && parsed.Equal(instant.Truncate(time.Microsecond))
}
