// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"net/http"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kubelocal"
)

// snapshotRuntimeRig is a Runtime that collects snapshots on a fake cluster. Collection is driven by the test: the
// periodic ticks come from a channel, and the queue signals each snapshot it is given.
type snapshotRuntimeRig struct {
	*runtimeRig
	queue         *fakeSnapshotQueue
	snapshotTicks chan time.Time
}

func newSnapshotRuntimeRig(t *testing.T) *snapshotRuntimeRig {
	t.Helper()

	queue := newFakeSnapshotQueue()
	snapshotTicks := make(chan time.Time)

	base := newRuntimeRig(t, func(opts *RuntimeOptions) {
		opts.Snapshots = queue
		opts.SnapshotTicks = snapshotTicks
		opts.Facts = func(context.Context) (kubelocal.Facts, bool) {
			return kubelocal.Facts{CaFingerprint: testFingerprint}, true
		}

		// The runtime counts error snapshots itself, whatever the caller gives the gate.
		opts.Gate.ConsecutiveErrorSnaps = func() int { return 0 }
	})

	seedHealthyCluster(base.api)

	return &snapshotRuntimeRig{runtimeRig: base, queue: queue, snapshotTicks: snapshotTicks}
}

// startSnapshotLoop runs the snapshot loop alone and returns what ends it and waits for it.
func (r *snapshotRuntimeRig) startSnapshotLoop() (stop func()) {
	r.t.Helper()

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})

	go func() {
		defer close(done)

		r.runtime.runSnapshots(ctx)
	}()

	return func() {
		cancel()
		<-done
	}
}

// tickSnapshots starts one periodic collection. The channel is unbuffered, so the send completes only when the loop
// is waiting again, which means every collection started earlier has finished.
func (r *snapshotRuntimeRig) tickSnapshots() {
	r.snapshotTicks <- r.clock.Now()
}

// keepLeadingFor lets d pass on the clock the way a running agent lets it pass, one election step at a time, so
// the holder renews the Lease throughout and is still the holder at the end.
func (r *snapshotRuntimeRig) keepLeadingFor(d time.Duration) {
	r.t.Helper()

	for elapsed := time.Duration(0); elapsed < d; elapsed += electionInterval {
		r.advance(electionInterval)
		r.step()
	}

	if r.leading() == false {
		r.t.Fatal("the runtime stopped leading while the clock ran")
	}
}

// Intent: a host that has just taken the Lease reads the cluster at once instead of waiting for the next
// quarter hour, and the snapshot it sends carries the transition count and the Lease epoch of the tenure it just
// won.
func TestRuntimeSnapshots_AnAcquisitionCollectsAtOnce(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.enrol()
	rig.step()

	if rig.leading() == false {
		t.Fatal("the runtime does not lead")
	}

	stop := rig.startSnapshotLoop()

	<-rig.queue.replaced

	stop()

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued = %d, want 1", rig.queue.count())
	}

	record := rig.queue.last(t)
	lease := rig.lease()

	if record.LeaseTransitions != 1 {
		t.Errorf("lease_transitions = %d, want 1", record.LeaseTransitions)
	}

	if want := stringAt(lease, "metadata", "uid"); (want == "") || (record.LeaseUid != want) {
		t.Errorf("lease_uid = %q, want the stored Lease's %q", record.LeaseUid, want)
	}

	if want := stringAt(lease, "metadata", "creationTimestamp"); (want == "") || (record.LeaseCreatedAt != want) {
		t.Errorf("lease_created_at = %q, want the stored Lease's %q", record.LeaseCreatedAt, want)
	}

	if record.KubeSystemUid != testKubeSystemUID || len(record.Nodes) != 2 || record.CaFingerprint != testFingerprint {
		t.Errorf("snapshot = %v, want the cluster, its two nodes and the fingerprint", record)
	}

	if rig.queue.items[0].itemType != db.TelemetryKubernetesCluster {
		t.Errorf("queued as type %d, want the cluster snapshot type", rig.queue.items[0].itemType)
	}
}

// Intent: a standby never reads the cluster, however many collections fall due: it makes no request to the
// cluster and queues nothing, so one host reads each cluster.
func TestRuntimeSnapshots_AStandbyNeverCollects(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.mutateLease(func(obj map[string]any) {
		spec := specOf(obj)
		spec["holderIdentity"] = "vord-someone-else"
		spec["renewTime"] = formatMicroTime(rig.clock.Now())
		spec["leaseDurationSeconds"] = int64(60)
	})
	rig.enrol()
	rig.step()

	if rig.leading() {
		t.Fatal("the runtime leads while another agent holds the Lease")
	}

	stop := rig.startSnapshotLoop()

	rig.tickSnapshots()
	rig.tickSnapshots()
	rig.tickSnapshots()

	stop()

	if rig.queue.count() != 0 {
		t.Errorf("snapshots queued by a standby = %d, want none", rig.queue.count())
	}

	for _, path := range []string{"/api/v1/nodes", "/api/v1/pods", "/version", "/api/v1/namespaces/kube-system"} {
		if got := rig.api.requestCount(http.MethodGet, path); got != 0 {
			t.Errorf("a standby read %s %d times, want never", path, got)
		}
	}
}

// Intent: the holder reads the cluster on every periodic tick but sends only what changed, or what has not been
// sent for half an hour, measured on the runtime's clock.
func TestRuntimeSnapshots_PeriodicCollectionsSendChangesAndAHeartbeat(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.enrol()
	rig.step()

	stop := rig.startSnapshotLoop()
	defer stop()

	<-rig.queue.replaced

	rig.tickSnapshots()
	rig.tickSnapshots()

	if got := rig.queue.count(); got != 1 {
		t.Fatalf("snapshots queued after unchanged collections = %d, want 1", got)
	}

	rig.api.setNodes([]map[string]any{testNode("only-node")})
	rig.tickSnapshots()
	rig.tickSnapshots()

	if got := rig.queue.count(); got != 2 {
		t.Fatalf("snapshots queued after the cluster changed = %d, want 2", got)
	}

	rig.keepLeadingFor(snapshotHeartbeat)
	rig.tickSnapshots()
	rig.tickSnapshots()

	if got := rig.queue.count(); got != 3 {
		t.Errorf("snapshots queued at the heartbeat = %d, want 3", got)
	}
}

// Intent: an acquisition makes the first snapshot of the new tenure go out even when the cluster is exactly as
// the last snapshot left it, because the queued copy of that one was dropped with the Lease.
func TestRuntimeSnapshots_AnAcquisitionAlwaysSendsTheFirstSnapshot(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.enrol()
	rig.step()

	stop := rig.startSnapshotLoop()
	defer stop()

	<-rig.queue.replaced

	rig.tickSnapshots()
	rig.tickSnapshots()

	if got := rig.queue.count(); got != 1 {
		t.Fatalf("snapshots queued before the acquisition = %d, want 1", got)
	}

	rig.runtime.handleAcquired()
	rig.tickSnapshots()
	rig.tickSnapshots()

	if got := rig.queue.count(); got != 2 {
		t.Errorf("snapshots queued after an acquisition = %d, want the unchanged cluster sent again", got)
	}

	rig.tickSnapshots()

	if got := rig.queue.count(); got != 2 {
		t.Errorf("snapshots queued after the next collection = %d, want the cluster not sent a third time", got)
	}
}

// Intent: announcing an acquisition never blocks the election step that makes it, however many announcements
// pile up before the loop reads one.
func TestRuntimeSnapshots_AnnouncingAnAcquisitionNeverBlocks(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)

	for i := 0; i < 5; i++ {
		rig.runtime.handleAcquired()
	}

	if got := len(rig.runtime.acquired); got != 1 {
		t.Errorf("pending announcements = %d, want them to merge into one", got)
	}
}

// Intent: the health gate fails a holder that has made three error snapshots in a row, and the runtime supplies
// that count, whatever the options carried. The holder then releases the Lease, drops its snapshots, and starts
// its next tenure with a clean count: if the count survived the release, the holder would fail its own gate on
// returning and never lead again.
func TestRuntimeSnapshots_ThreeErrorSnapshotsReleaseTheLeaseAndTheNextTenureStartsClean(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: -1})
	rig.enrol()
	rig.step()

	if rig.leading() == false {
		t.Fatal("the runtime does not lead")
	}

	for i := 1; i <= 3; i++ {
		rig.runtime.collectSnapshot(context.Background())

		if got := rig.runtime.snapshots.consecutiveErrors(); got != i {
			t.Fatalf("consecutive error snapshots after %d failures = %d", i, got)
		}
	}

	if rig.queue.last(t).ReporterError != reasonAPIUnreachable {
		t.Errorf("the snapshot was %v, want an api_unreachable error snapshot", rig.queue.last(t))
	}

	rig.advance(electionInterval)
	rig.step()

	if got := rig.holder(); got != "" {
		t.Fatalf("holder = %q, want the Lease released after three error snapshots", got)
	}

	if rig.lost != 1 {
		t.Errorf("snapshots were dropped %d times, want 1", rig.lost)
	}

	if got := rig.runtime.snapshots.consecutiveErrors(); got != 0 {
		t.Fatalf("consecutive error snapshots after the release = %d, want the count cleared", got)
	}

	rig.advance(releaseBackoff)
	rig.step()

	if rig.leading() == false {
		t.Fatalf("the holder does not lead again after the backoff, holder = %q: the old error count must not keep failing its gate", rig.holder())
	}

	if got := rig.specNumber("leaseTransitions"); got != 2 {
		t.Errorf("spec.leaseTransitions = %d, want 2 after the second tenure", got)
	}
}

// Intent: a good snapshot between failures clears the count the gate reads, so only failures in a row release
// the Lease.
func TestRuntimeSnapshots_AGoodSnapshotClearsTheErrorCount(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.api.inject(fault{method: http.MethodGet, path: "/api/v1/nodes", status: http.StatusInternalServerError, reason: "InternalError", message: "boom", times: 2})
	rig.enrol()
	rig.step()

	rig.runtime.collectSnapshot(context.Background())
	rig.runtime.collectSnapshot(context.Background())

	if got := rig.runtime.snapshots.consecutiveErrors(); got != 2 {
		t.Fatalf("consecutive error snapshots = %d, want 2", got)
	}

	rig.runtime.collectSnapshot(context.Background())

	if got := rig.runtime.snapshots.consecutiveErrors(); got != 0 {
		t.Errorf("consecutive error snapshots after a good one = %d, want 0", got)
	}

	rig.advance(electionInterval)
	rig.step()

	if rig.leading() == false {
		t.Error("the holder lost the Lease although its last snapshot was good")
	}
}

// Intent: losing the Lease drops the snapshots the tenure queued and makes the loop forget what it sent, whatever
// way the loss came about.
func TestRuntimeSnapshots_LosingTheLeaseDropsTheQueueAndForgetsWhatWasSent(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.enrol()
	rig.step()

	rig.runtime.collectSnapshot(context.Background())

	if rig.queue.count() != 1 {
		t.Fatalf("snapshots queued = %d, want 1", rig.queue.count())
	}

	rig.paused = true
	rig.advance(electionInterval)
	rig.step()

	if rig.lost != 1 {
		t.Fatalf("snapshots were dropped %d times, want 1", rig.lost)
	}

	rig.runtime.snapshots.mu.Lock()
	sent := rig.runtime.snapshots.sent
	rig.runtime.snapshots.mu.Unlock()

	if sent {
		t.Error("the loop still remembers the snapshot of the tenure that ended")
	}
}

// Intent: the runtime does what the shutdown needs: when the agent stops, the snapshot loop is ended and joined
// before the Lease is released, and a holder that acquired during the run has collected by then.
func TestRuntimeSnapshots_RunCollectsOnAcquisitionAndStopsTheLoopOnShutdown(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)

	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	rig.tickAndSettle()

	rig.enrol()
	rig.advance(electionInterval)
	rig.tickAndSettle()
	rig.tickAndSettle()

	<-rig.queue.replaced

	cancel()
	<-done

	if got := rig.holder(); got != "" {
		t.Errorf("holder = %q after shutdown, want the Lease released", got)
	}

	queued := rig.queue.count()
	requests := len(rig.api.requestLog())

	rig.runtime.handleAcquired()

	if rig.queue.count() != queued || len(rig.api.requestLog()) != requests {
		t.Error("the cluster was read after the runtime stopped")
	}
}

// Intent: a runtime given no queue still takes part in the election but never reads the cluster.
func TestRuntimeSnapshots_WithoutAQueueNothingIsCollected(t *testing.T) {
	rig := newRuntimeRig(t)
	seedHealthyCluster(rig.api)

	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	rig.tickAndSettle()

	rig.enrol()
	rig.advance(electionInterval)
	rig.tickAndSettle()
	rig.tickAndSettle()

	if rig.leading() == false {
		t.Fatal("the runtime does not lead")
	}

	cancel()
	<-done

	if got := rig.api.requestCount(http.MethodGet, "/api/v1/nodes"); got != 0 {
		t.Errorf("nodes were listed %d times with no queue to put a snapshot in", got)
	}
}

// panickingQueue fails the way a bug would.
type panickingQueue struct{}

func (panickingQueue) ReplaceTelemetryOfType(string, db.TelemetryType, string) error {
	panic("the queue blew up")
}

// Intent: a collection that panics is logged and survived, so the election and the loop carry on.
func TestRuntimeSnapshots_APanicDuringCollectionDoesNotEscape(t *testing.T) {
	logs := captureLogs(t)
	rig := newSnapshotRuntimeRig(t)
	rig.runtime.snapshots.queue = panickingQueue{}
	rig.enrol()
	rig.step()

	rig.runtime.collectSnapshot(context.Background())

	if got := logs.String(); got == "" {
		t.Error("the panic was not logged")
	}
}

// Intent: with no running elector (the host is not enrolled) a collection that falls due does nothing.
func TestRuntimeSnapshots_WithoutAnElectorACollectionDoesNothing(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)

	rig.runtime.collectSnapshot(context.Background())

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests with no elector = %d, want none", got)
	}
}

// Intent: the periodic collection runs on a 15 minute ticker when none is injected, and the loop ends with its
// context.
func TestRuntimeSnapshots_WithTheRealTickerTheLoopStopsOnCancel(t *testing.T) {
	rig := newSnapshotRuntimeRig(t)
	rig.runtime.snapshotTicks = nil

	stop := rig.startSnapshotLoop()
	stop()
}
