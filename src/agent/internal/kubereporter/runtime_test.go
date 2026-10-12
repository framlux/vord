// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
)

const runtimeInstanceID = "0123456789abcdef"

// runtimeRig is a Runtime on a fake cluster, with a data directory the test enrols into.
type runtimeRig struct {
	*electorRig
	dataDir string
	runtime *Runtime
	paused  bool
	lost    int
	ticks   chan time.Time
}

func newRuntimeRig(t *testing.T, adjust ...func(opts *RuntimeOptions)) *runtimeRig {
	t.Helper()

	rig := &runtimeRig{
		electorRig: newElectorRig(t),
		dataDir:    t.TempDir(),
		ticks:      make(chan time.Time),
	}

	opts := RuntimeOptions{
		DataDir: rig.dataDir,
		Clock:   rig.clock,
		Gate: Gate{
			Paused:    func() bool { return rig.paused },
			LastAck:   rig.clock.Now,
			StartedAt: rig.clock.Now(),
		},
		OnLost: func() { rig.lost++ },
		Ticks:  rig.ticks,
	}

	for _, change := range adjust {
		change(&opts)
	}

	rig.runtime = NewRuntime(opts)

	return rig
}

// enrol writes a state file holding a fresh reporter token and returns the state.
func (r *runtimeRig) enrol() State {
	r.t.Helper()

	return r.enrolWithToken(r.api.issueReporterToken())
}

func (r *runtimeRig) enrolWithToken(token string) State {
	r.t.Helper()

	state := State{InstanceID: runtimeInstanceID, Server: r.api.url(), CACert: string(r.api.caPEM), Token: token}
	if err := SaveState(r.dataDir, state); err != nil {
		r.t.Fatalf("SaveState: %v", err)
	}

	return state
}

func (r *runtimeRig) step() {
	r.t.Helper()

	r.runtime.step(context.Background())
}

// leading reports whether the runtime's elector holds the Lease.
func (r *runtimeRig) leading() bool {
	r.t.Helper()

	r.runtime.mu.Lock()
	cur := r.runtime.current
	r.runtime.mu.Unlock()

	if cur == nil {
		return false
	}

	_, ok := cur.elector.IsLeader()

	return ok
}

// running reports whether the runtime has an elector.
func (r *runtimeRig) running() bool {
	r.runtime.mu.Lock()
	defer r.runtime.mu.Unlock()

	return r.runtime.current != nil
}

// Intent: with no state file the host is not enrolled, so the runtime must do nothing at all, and the moment a
// state file appears (an enrol) it must build its client and start contending, with no agent restart.
func TestRuntime_StartsWhenTheStateFileAppears(t *testing.T) {
	rig := newRuntimeRig(t)

	rig.step()

	if rig.running() {
		t.Fatal("the runtime started an elector without a state file")
	}

	if got := len(rig.api.requestLog()); got != 0 {
		t.Fatalf("requests without a state file = %d, want none", got)
	}

	rig.enrol()
	rig.advance(electionInterval)
	rig.step()

	if rig.leading() == false {
		t.Fatal("the runtime does not lead after the state file appeared")
	}

	if got := rig.holder(); got != "vord-"+runtimeInstanceID {
		t.Errorf("holder = %q, want vord-%s", got, runtimeInstanceID)
	}
}

// Intent: the reporter's own token, from the state file, is what talks to the cluster, and the credentials are
// never anything else: the Lease calls come from the principal that holds the reporter token.
func TestRuntime_UsesTheStateFilesToken(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()

	rig.step()

	for _, request := range rig.api.requestLog() {
		if request.Principal != "reporter" {
			t.Errorf("%s %s was made as %q, want the reporter", request.Method, request.Path, request.Principal)
		}
	}
}

// Intent: removing the state file (an unenrol the agent was not told about, or a manual removal) stops the
// reporter and hands the Lease back, so a standby does not wait for it to expire; enrolling again starts it again.
func TestRuntime_StopsAndReleasesWhenTheStateFileIsGone(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	if rig.leading() == false {
		t.Fatal("the runtime does not lead")
	}

	if err := DeleteState(rig.dataDir); err != nil {
		t.Fatalf("DeleteState: %v", err)
	}

	rig.advance(electionInterval)
	rig.step()

	if rig.running() {
		t.Error("the runtime kept its elector after the state file was removed")
	}

	if got := rig.holder(); got != "" {
		t.Errorf("holder = %q, want the Lease released", got)
	}

	if rig.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", rig.lost)
	}

	rig.enrol()
	rig.advance(electionInterval)
	rig.step()

	if rig.leading() == false {
		t.Error("the runtime does not lead after a second enrol")
	}

	if got := rig.specNumber("leaseTransitions"); got != 2 {
		t.Errorf("spec.leaseTransitions = %d, want 2 for the second tenure", got)
	}
}

// Intent: a re-enrol replaces the token (and may replace the CA), so the runtime must drop the old elector and
// build a new one from the new credentials, and the new token is the one that talks to the cluster.
func TestRuntime_RestartsWhenTheCredentialsChange(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	before := rig.runtime.current

	second := rig.enrol()
	rig.advance(electionInterval)
	rig.step()

	if rig.runtime.current == before {
		t.Fatal("the runtime kept the elector built from the old credentials")
	}

	if rig.runtime.current.state != second {
		t.Error("the new elector was not built from the new state")
	}

	if rig.leading() == false {
		t.Error("the runtime does not lead on the new credentials")
	}
}

// Intent: an unchanged state file must not rebuild anything, or the election would restart every 20 seconds and
// never keep the Lease.
func TestRuntime_AnUnchangedStateKeepsTheSameElector(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	before := rig.runtime.current

	for i := 0; i < 3; i++ {
		rig.advance(electionInterval)
		rig.step()
	}

	if rig.runtime.current != before {
		t.Error("the runtime rebuilt its elector although the state did not change")
	}

	if got := rig.specNumber("leaseTransitions"); got != 1 {
		t.Errorf("spec.leaseTransitions = %d, want 1: the same tenure all along", got)
	}
}

// Intent: a state file that cannot be read right now must not stop a working reporter or hand its Lease back;
// the reporter carries on with what it has.
func TestRuntime_AnUnreadableStateLeavesTheReporterRunning(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	if err := os.WriteFile(filepath.Join(rig.dataDir, StateFileName), []byte("{not json"), 0o600); err != nil {
		t.Fatalf("corrupt the state file: %v", err)
	}

	rig.advance(electionInterval)
	rig.step()

	if rig.leading() == false {
		t.Error("the runtime dropped the Lease over an unreadable state file")
	}

	if rig.lost != 0 {
		t.Errorf("onLost ran %d times, want 0", rig.lost)
	}
}

// Intent: unenrol and uninstall hand the Lease back through ReleaseAndStop before they delete the token, and the
// loop must not take it again in the time they still need. It heals itself if the command failed and the host is
// still enrolled once the hold-off has passed.
func TestRuntime_ReleaseAndStop_ReleasesAndHoldsOffForALeaseDuration(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	rig.runtime.ReleaseAndStop(context.Background())

	if rig.running() {
		t.Error("the runtime kept its elector after ReleaseAndStop")
	}

	if got := rig.holder(); got != "" {
		t.Errorf("holder = %q, want the Lease released", got)
	}

	if rig.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", rig.lost)
	}

	if got := rig.runtime.ReporterError(); got != "" {
		t.Errorf("ReporterError = %q after stopping, want none", got)
	}

	requests := len(rig.api.requestLog())

	rig.advance(electionInterval)
	rig.step()
	rig.advance(39 * time.Second)
	rig.step()

	if rig.running() {
		t.Error("the loop restarted the elector during the hold-off")
	}

	if got := len(rig.api.requestLog()); got != requests {
		t.Errorf("made %d requests during the hold-off, want none", got-requests)
	}

	rig.advance(time.Second)
	rig.step()

	if rig.leading() == false {
		t.Error("the runtime did not recover once the hold-off passed with the host still enrolled")
	}
}

// Intent: the hold-off protects the credentials that were released, not the host: a new enrol during it brings
// new credentials and starts contending at once.
func TestRuntime_ReleaseAndStop_DoesNotHoldOffNewCredentials(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	rig.runtime.ReleaseAndStop(context.Background())

	rig.enrol()
	rig.advance(electionInterval)
	rig.step()

	if rig.leading() == false {
		t.Error("new credentials were held off")
	}
}

// Intent: the elector may not have started yet when a command arrives, for example just after an enrol. The
// release must still keep the loop from starting one for the state file that the command is about to delete.
func TestRuntime_ReleaseAndStop_WithNoElectorStillHoldsOffTheStateOnDisk(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()

	rig.runtime.ReleaseAndStop(context.Background())
	rig.step()

	if rig.running() {
		t.Error("the loop started an elector right after ReleaseAndStop")
	}

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests = %d, want none", got)
	}
}

// Intent: a host that is not enrolled, or has no runtime running yet, can be told to release without anything
// going wrong.
func TestRuntime_ReleaseAndStop_WithNothingToRelease(t *testing.T) {
	rig := newRuntimeRig(t)

	rig.runtime.ReleaseAndStop(context.Background())
	rig.runtime.ReleaseAndStop(context.Background())

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests = %d, want none", got)
	}
}

// Intent: a release must be able to interrupt a loop step that is waiting on a slow API, or the command that
// asked for it would wait for that request too. While a step runs it publishes a way to end it, and
// ReleaseAndStop uses it.
func TestRuntime_ReleaseAndStop_EndsAStepInFlight(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()

	cancelled := 0
	rig.runtime.mu.Lock()
	rig.runtime.cancelStep = func() { cancelled++ }
	rig.runtime.mu.Unlock()

	rig.runtime.ReleaseAndStop(context.Background())

	if cancelled != 1 {
		t.Errorf("ReleaseAndStop ended the step in flight %d times, want 1", cancelled)
	}

	// While a step runs, the hook is set; the Lease requests it makes run under the step's context.
	rig = newRuntimeRig(t)
	rig.enrol()
	rig.step()

	sawHook := false

	rig.runtime.current.elector.withTimeout = func(ctx context.Context, d time.Duration) (context.Context, context.CancelFunc) {
		rig.runtime.mu.Lock()
		sawHook = sawHook || (rig.runtime.cancelStep != nil)
		rig.runtime.mu.Unlock()

		return context.WithTimeout(ctx, d)
	}

	rig.advance(electionInterval)
	rig.step()

	if sawHook == false {
		t.Error("a step in flight did not publish how to end it")
	}

	if rig.runtime.cancelStep != nil {
		t.Error("the hook to end the step was left set after the step finished")
	}
}

// Intent: a token the cluster rejects is surfaced through the runtime, for the local node report, and the
// report goes away with the reporter.
func TestRuntime_ReporterError_ComesFromTheElector(t *testing.T) {
	rig := newRuntimeRig(t)

	if got := rig.runtime.ReporterError(); got != "" {
		t.Fatalf("ReporterError before enrolling = %q, want none", got)
	}

	rig.enrolWithToken("a-token-the-cluster-never-issued")
	rig.step()

	if got := rig.runtime.ReporterError(); got != "credentials_rejected" {
		t.Errorf("ReporterError = %q, want credentials_rejected", got)
	}

	if err := DeleteState(rig.dataDir); err != nil {
		t.Fatalf("DeleteState: %v", err)
	}

	rig.step()

	if got := rig.runtime.ReporterError(); got != "" {
		t.Errorf("ReporterError after unenrolling = %q, want none", got)
	}
}

// Intent: an error the elector learned belongs to the credentials it was built from. Once the state file holds
// other credentials (a re-enrol wrote them ahead of the loop's next pass), the old error describes a token that
// no longer exists and must not be reported, and the new credentials start clean.
func TestRuntime_ReporterError_IsNotReportedForCredentialsThatWereReplaced(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrolWithToken("a-token-the-cluster-never-issued")
	rig.step()

	if got := rig.runtime.ReporterError(); got != "credentials_rejected" {
		t.Fatalf("ReporterError = %q, want credentials_rejected before the re-enrol", got)
	}

	rig.enrol()

	if got := rig.runtime.ReporterError(); got != "" {
		t.Errorf("ReporterError after the state file was replaced = %q, want none", got)
	}

	rig.advance(electionInterval)
	rig.step()

	if got := rig.runtime.ReporterError(); got != "" {
		t.Errorf("ReporterError once the elector runs on the new credentials = %q, want none", got)
	}
}

// Intent: a state file that cannot be read right now says nothing about the credentials, so the elector's own
// error stays, as the rest of the runtime keeps a working reporter over a file that may be mid-rewrite.
func TestRuntime_ReporterError_SurvivesAnUnreadableStateFile(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrolWithToken("a-token-the-cluster-never-issued")
	rig.step()

	if err := os.WriteFile(filepath.Join(rig.dataDir, StateFileName), []byte("{not json"), 0o600); err != nil {
		t.Fatalf("corrupt the state file: %v", err)
	}

	if got := rig.runtime.ReporterError(); got != "credentials_rejected" {
		t.Errorf("ReporterError = %q, want credentials_rejected while the state file is unreadable", got)
	}
}

// Intent: the health gate reaches the elector, so a paused host's runtime hands the Lease back like any holder
// whose gate fails.
func TestRuntime_PassesTheGateToTheElector(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()
	rig.step()

	rig.paused = true
	rig.advance(electionInterval)
	rig.step()

	if got := rig.holder(); got != "" {
		t.Errorf("holder = %q, want the paused host to have released the Lease", got)
	}

	if rig.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", rig.lost)
	}
}

// Intent: credentials the client cannot be built from (here a CA that is not a certificate) must not crash the
// runtime or send a request, and must be tried again on the next tick.
func TestRuntime_UnusableCredentialsMakeNoRequests(t *testing.T) {
	rig := newRuntimeRig(t)

	state := State{InstanceID: runtimeInstanceID, Server: rig.api.url(), CACert: "this is not a certificate", Token: "token"}
	if err := SaveState(rig.dataDir, state); err != nil {
		t.Fatalf("SaveState: %v", err)
	}

	rig.step()
	rig.advance(electionInterval)
	rig.step()

	if rig.leading() {
		t.Error("the runtime leads on credentials that cannot be used")
	}

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests = %d, want none", got)
	}
}

// startRun runs the loop in the background and returns what is needed to end it and wait for it.
func (r *runtimeRig) startRun(ctx context.Context) <-chan struct{} {
	done := make(chan struct{})

	go func() {
		defer close(done)

		r.runtime.Run(ctx)
	}()

	return done
}

// tickAndSettle starts one more step of the loop. The channel is unbuffered, so the send completes only when the
// loop is back at its wait, which means every step started by an earlier send has finished.
func (r *runtimeRig) tickAndSettle() {
	r.ticks <- r.clock.Now()
}

// Intent: the loop picks up an enrol on its next tick without a restart, keeps the Lease across ticks, and on a
// graceful shutdown hands the Lease back even though the context that stopped it is already cancelled.
func TestRuntime_Run_PicksUpAnEnrolAndReleasesOnShutdown(t *testing.T) {
	rig := newRuntimeRig(t)
	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	// The first step ran before the loop waited for a tick; it found no state file.
	rig.tickAndSettle()

	rig.enrol()
	rig.advance(electionInterval)
	rig.tickAndSettle()
	rig.tickAndSettle()

	if got := rig.holder(); got != "vord-"+runtimeInstanceID {
		t.Fatalf("holder = %q, want the loop to have acquired the Lease after the enrol", got)
	}

	cancel()
	<-done

	if got := rig.holder(); got != "" {
		t.Errorf("holder = %q after shutdown, want the Lease released", got)
	}

	if rig.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", rig.lost)
	}

	if rig.running() {
		t.Error("the runtime kept its elector after Run returned")
	}
}

// Intent: a loop whose context is already cancelled must not start anything, and must return.
func TestRuntime_Run_WithACancelledContextDoesNothing(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	done := rig.startRun(ctx)
	<-done

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests = %d, want none", got)
	}
}

// Intent: with no injected ticks the loop runs on its own 20 second ticker and still stops on its context.
func TestRuntime_Run_WithTheRealTickerStopsOnCancel(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.runtime.ticks = nil

	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	cancel()
	<-done
}

// Intent: the options the agent passes are the ones documented: no clock means the system clock.
func TestNewRuntime_DefaultsToTheSystemClock(t *testing.T) {
	runtime := NewRuntime(RuntimeOptions{DataDir: t.TempDir()})

	if _, ok := runtime.clock.(systemClock); ok == false {
		t.Errorf("clock = %T, want the system clock", runtime.clock)
	}

	if now := runtime.clock.Now(); time.Since(now) > time.Minute {
		t.Errorf("the system clock reads %v", now)
	}
}

// Intent: a snapshot left in the queue by a previous run, for example after a crash while leading, belongs to a
// tenure nothing vouches for any more. The runtime drops it when it starts on an enrolled host, before its first
// election request, since a new holder collects at once anyway.
func TestRuntime_Run_DropsSnapshotsOfAPreviousRunBeforeTheFirstRequest(t *testing.T) {
	rig := newRuntimeRig(t)
	rig.enrol()

	var requestsAtDrop []int

	rig.runtime.onLost = func() {
		requestsAtDrop = append(requestsAtDrop, len(rig.api.requestLog()))
	}

	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	// The first step has finished once the loop takes a tick.
	rig.tickAndSettle()

	if got := rig.holder(); got != "vord-"+runtimeInstanceID {
		t.Fatalf("holder = %q, want the loop to have taken the Lease", got)
	}

	cancel()
	<-done

	if len(requestsAtDrop) != 2 {
		t.Fatalf("snapshots were dropped %d times, want at the start and again when the Lease was released: %v", len(requestsAtDrop), requestsAtDrop)
	}

	if requestsAtDrop[0] != 0 {
		t.Errorf("the start-up drop came after %d election requests, want before the first", requestsAtDrop[0])
	}
}

// Intent: the drop reaches the real queue: cluster snapshots a previous run left pending or in flight are gone
// when the runtime has started, and nothing else is touched.
func TestRuntime_Run_StartUpDropRemovesQueuedClusterSnapshots(t *testing.T) {
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open database: %v", err)
	}

	t.Cleanup(func() { database.Close() })

	store := db.NewStore(database, 0)

	for id, itemType := range map[string]db.TelemetryType{
		"snapshot-1": db.TelemetryKubernetesCluster,
		"snapshot-2": db.TelemetryKubernetesCluster,
		"node":       db.TelemetryKubernetesNode,
	} {
		if err := store.EnqueueTelemetry(id, itemType, `{}`); err != nil {
			t.Fatalf("enqueue %s: %v", id, err)
		}
	}

	if _, err := store.DequeueTelemetryByTypes([]db.TelemetryType{db.TelemetryKubernetesCluster}, 1); err != nil {
		t.Fatalf("dequeue: %v", err)
	}

	rig := newRuntimeRig(t)
	rig.enrol()

	rig.runtime.onLost = func() {
		if _, err := store.DeleteTelemetryByType(db.TelemetryKubernetesCluster); err != nil {
			t.Errorf("DeleteTelemetryByType: %v", err)
		}
	}

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	// A loop that never starts must not drop anything.
	<-rig.startRun(ctx)

	var snapshots int

	if err := store.DB().QueryRow("SELECT COUNT(*) FROM telemetry_queue WHERE item_type = ?", int(db.TelemetryKubernetesCluster)).Scan(&snapshots); err != nil {
		t.Fatalf("count: %v", err)
	}

	if snapshots != 2 {
		t.Fatalf("snapshots after a loop that never started = %d, want 2 untouched", snapshots)
	}

	ctx, cancel = context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	// The first step has finished once the loop takes a tick, and the Lease is still held: nothing has been
	// released yet, so only the start-up drop can have emptied the queue.
	rig.tickAndSettle()

	if err := store.DB().QueryRow("SELECT COUNT(*) FROM telemetry_queue WHERE item_type = ?", int(db.TelemetryKubernetesCluster)).Scan(&snapshots); err != nil {
		t.Fatalf("count: %v", err)
	}

	if snapshots != 0 {
		t.Errorf("cluster snapshots left after the runtime started = %d, want none", snapshots)
	}

	if got := rig.holder(); got != "vord-"+runtimeInstanceID {
		t.Errorf("holder = %q, want the runtime to be holding the Lease", got)
	}

	cancel()
	<-done

	var others int

	if err := store.DB().QueryRow("SELECT COUNT(*) FROM telemetry_queue WHERE item_type = ?", int(db.TelemetryKubernetesNode)).Scan(&others); err != nil {
		t.Fatalf("count: %v", err)
	}

	if others != 1 {
		t.Errorf("node reports left = %d, want 1", others)
	}
}

// Intent: a host that is not enrolled has nothing from a previous tenure to drop, so starting the runtime on it
// announces nothing.
func TestRuntime_Run_NoStartUpDropWithoutAStateFile(t *testing.T) {
	rig := newRuntimeRig(t)

	rig.runtime.onLost = func() { rig.lost++ }

	ctx, cancel := context.WithCancel(context.Background())
	done := rig.startRun(ctx)

	rig.tickAndSettle()
	cancel()
	<-done

	if rig.lost != 0 {
		t.Errorf("snapshots were dropped %d times on a host that is not enrolled, want 0", rig.lost)
	}
}
