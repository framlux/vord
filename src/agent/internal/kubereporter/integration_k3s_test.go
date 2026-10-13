// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

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
	s.phase(t, "interrupted_enrol_is_completed_by_the_next_enrol", s.interruptedEnrol)
	s.phase(t, "stale_lease_write_conflicts", s.staleLeaseWrite)
	s.phase(t, "delete_preconditions_hold", s.deletePreconditions)
	s.phase(t, "missing_lease_is_not_found_for_the_reporter", s.missingLease)
	s.phase(t, "terminating_namespace", s.terminatingNamespace)
	s.phase(t, "custom_resource_keeps_the_namespace", s.customResourceKeepsNamespace)
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

// interruptedEnrol enrols a third host through a proxy that cuts off its API access as soon as the Secret exists.
// The enrol fails with the Secret in the cluster and no state file, and it cannot delete the Secret either, so the
// pending file is the only thing on the host that names it. A following enrol with working access must find that
// id, replace the Secret instead of leaking it, and leave exactly one Secret for the host, a state file that holds
// the id and no pending file.
func (s *scenario) interruptedEnrol(t *testing.T) {
	c := newCandidate(t, "c", s.cluster, s.clock)
	t.Cleanup(func() { c.stop(t) })

	proxy := newInterruptingProxy(t, s.cluster)

	facts, _ := s.cluster.factsSource()(context.Background())
	facts.AdminKubeconfigPath = proxy.kubeconfig(t)

	interrupted := kubereporter.New(kubereporter.Options{
		DataDir: c.dataDir,
		Facts:   func(context.Context) (kubelocal.Facts, bool) { return facts, true },
	})

	ctx, cancel := context.WithTimeout(context.Background(), commandTimeout)
	defer cancel()

	result := interrupted.Enrol(ctx)
	if result.Success {
		t.Fatalf("an enrol whose connection was cut after the Secret was created succeeded: %+v", result)
	}

	t.Logf("the interrupted enrol answered %q", result.Message)

	pendingFile := filepath.Join(c.dataDir, kubereporter.PendingFileName)

	data, err := os.ReadFile(pendingFile)
	if err != nil {
		t.Fatalf("the interrupted enrol left no pending file: %v", err)
	}

	var pending struct {
		InstanceID string `json:"instance_id"`
	}

	if err := json.Unmarshal(data, &pending); err != nil {
		t.Fatalf("the pending file is not JSON: %v", err)
	}

	if _, found, err := kubereporter.LoadState(c.dataDir); (err != nil) || found {
		t.Errorf("the interrupted enrol left a state file (found=%v, err=%v)", found, err)
	}

	if kubereporter.LocalRecordPresent(c.dataDir) {
		t.Error("a host whose enrol was interrupted holds only a pending file, which is not a local record")
	}

	secret := secretsPath + "/" + tokenSecretPrefix + pending.InstanceID
	s.cluster.mustGet(t, secret)

	result = c.reporter.Enrol(ctx)
	if (result.Success == false) || (result.Message != "enrolled") {
		t.Fatalf("the enrol that follows an interrupted one answered %+v", result)
	}

	if got := c.instanceID(t); got != pending.InstanceID {
		t.Errorf("the follow-up enrol used instance id %q, want the recorded %q", got, pending.InstanceID)
	}

	if _, err := os.Stat(pendingFile); os.IsNotExist(err) == false {
		t.Errorf("the pending file survived the enrol that completed it: %v", err)
	}

	secrets, err := s.cluster.names(secretsPath, true)
	if err != nil {
		t.Fatalf("list the Secrets: %v", err)
	}

	own := 0

	for _, name := range secrets {
		if name == tokenSecretPrefix+pending.InstanceID {
			own++
		}
	}

	if own != 1 {
		t.Errorf("the cluster holds %d token Secrets for the interrupted host, want exactly one (all: %v)", own, secrets)
	}

	s.cluster.requireInstalled(t, s.a.instanceID(t), s.b.instanceID(t), pending.InstanceID)

	unenrol, cancelUnenrol := context.WithTimeout(context.Background(), commandTimeout)
	defer cancelUnenrol()

	if result := c.reporter.Unenrol(unenrol); result.Success == false {
		t.Fatalf("Unenrol of the completed host = %+v", result)
	}

	s.cluster.requireInstalled(t, s.a.instanceID(t), s.b.instanceID(t))
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
