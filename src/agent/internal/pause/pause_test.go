// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package pause

import (
	"bytes"
	"log/slog"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	pb "github.com/framlux/vord/internal/proto/agent"
)

// fakeClock is a manually advanced clock so lease expiry never depends on wall-clock time.
type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

func newFakeClock() *fakeClock {
	return &fakeClock{now: time.Unix(0, 0)}
}

func (f *fakeClock) Now() time.Time {
	f.mu.Lock()
	defer f.mu.Unlock()

	return f.now
}

func (f *fakeClock) Advance(d time.Duration) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.now = f.now.Add(d)
}

// hookRecorder counts hook invocations and remembers the reason OnPause was given.
type hookRecorder struct {
	pauses  atomic.Int32
	resumes atomic.Int32
	reasons []Reason
	events  []string
	mu      sync.Mutex

	// inFlight counts hooks executing right now, and overlaps counts the times one started while another was
	// still running. Each hook yields while it is "in flight" so an overlap has a chance to show itself.
	inFlight atomic.Int32
	overlaps atomic.Int32
}

// enter marks a hook as executing; the returned func marks it finished.
func (h *hookRecorder) enter() func() {
	if h.inFlight.Add(1) > 1 {
		h.overlaps.Add(1)
	}
	for i := 0; i < 3; i++ {
		runtime.Gosched()
	}

	return func() { h.inFlight.Add(-1) }
}

func (h *hookRecorder) hooks() Hooks {
	return Hooks{
		OnPause: func(reason Reason) {
			defer h.enter()()
			h.mu.Lock()
			h.reasons = append(h.reasons, reason)
			h.events = append(h.events, "pause")
			h.mu.Unlock()
			h.pauses.Add(1)
		},
		OnResume: func() {
			defer h.enter()()
			h.mu.Lock()
			h.events = append(h.events, "resume")
			h.mu.Unlock()
			h.resumes.Add(1)
		},
	}
}

func fixedInterval(d time.Duration) func() time.Duration {
	return func() time.Duration { return d }
}

// Intent: the first paused signal pauses the host and runs OnPause once with the reason; a repeat of the
// same signal renews the lease but is not a new transition.
func TestObserve_HostPaused_PausesAndRunsOnPauseOnce(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)

	if c.Paused() == false {
		t.Fatal("expected paused after Observe(HostPaused)")
	}
	if rec.pauses.Load() != 1 {
		t.Fatalf("expected OnPause once, got %d", rec.pauses.Load())
	}
	if len(rec.reasons) != 1 || rec.reasons[0] != HostPaused {
		t.Errorf("expected OnPause reason HostPaused, got %v", rec.reasons)
	}

	c.Observe(HostPaused)

	if rec.pauses.Load() != 1 {
		t.Errorf("expected OnPause not to run again on a repeated signal, got %d calls", rec.pauses.Load())
	}
}

// Intent: an inactive subscription is a pause reason in its own right and reaches OnPause as such.
func TestObserve_SubscriptionInactive_IsAPauseReason(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(SubscriptionInactive)

	if c.Paused() == false {
		t.Fatal("expected paused after Observe(SubscriptionInactive)")
	}
	if rec.pauses.Load() != 1 {
		t.Fatalf("expected OnPause once, got %d", rec.pauses.Load())
	}
	if rec.reasons[0] != SubscriptionInactive {
		t.Errorf("expected OnPause reason SubscriptionInactive, got %v", rec.reasons[0])
	}
}

// Intent: a ping that reports no pause state ends the pause and runs OnResume exactly once.
func TestObserve_NoneAfterPause_Resumes(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)
	c.Observe(None)

	if c.Paused() {
		t.Fatal("expected not paused after Observe(None)")
	}
	if rec.resumes.Load() != 1 {
		t.Fatalf("expected OnResume once, got %d", rec.resumes.Load())
	}

	c.Observe(None)

	if rec.resumes.Load() != 1 {
		t.Errorf("expected a second Observe(None) not to run OnResume again, got %d calls", rec.resumes.Load())
	}
}

// Intent: an agent that was never paused does nothing when pings report no state.
func TestObserve_NoneWhenNeverPaused_DoesNothing(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(None)

	if c.Paused() {
		t.Error("expected a never-paused coordinator to stay unpaused")
	}
	if rec.pauses.Load() != 0 || rec.resumes.Load() != 0 {
		t.Errorf("expected no hooks, got pauses=%d resumes=%d", rec.pauses.Load(), rec.resumes.Load())
	}
}

// Intent: the pause is a lease of three live ping intervals, so a lost resume cannot strand the host.
func TestPaused_LeaseExpiry_ResumesAfterThreeIntervals(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)

	clock.Advance(179 * time.Second)
	if c.Paused() == false {
		t.Fatal("expected still paused at 179s of a 180s lease")
	}
	if rec.resumes.Load() != 0 {
		t.Fatalf("expected no OnResume before expiry, got %d", rec.resumes.Load())
	}

	clock.Advance(2 * time.Second)
	if c.Paused() {
		t.Fatal("expected the lease to have expired at 181s")
	}
	if rec.resumes.Load() != 1 {
		t.Fatalf("expected OnResume once on expiry, got %d", rec.resumes.Load())
	}

	if c.Paused() {
		t.Error("expected to stay resumed on a later check")
	}
	if rec.resumes.Load() != 1 {
		t.Errorf("expected OnResume not to run again, got %d calls", rec.resumes.Load())
	}
}

// Intent: the lease is held for the full three intervals; it only lapses once strictly more time than that passes.
func TestPaused_LeaseBoundary_ExactlyThreeIntervalsIsStillPaused(t *testing.T) {
	clock := newFakeClock()
	c := New(clock.Now, fixedInterval(60*time.Second), Hooks{})

	c.Observe(HostPaused)
	clock.Advance(180 * time.Second)

	if c.Paused() == false {
		t.Error("expected a lease of exactly three intervals to still hold")
	}
}

// Intent: every ping that still carries a pause state renews the lease from that moment.
func TestObserve_Renewal_ExtendsLease(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)
	clock.Advance(170 * time.Second)
	c.Observe(HostPaused)
	clock.Advance(170 * time.Second)

	if c.Paused() == false {
		t.Error("expected the renewed lease to still hold 170s after the renewal")
	}
	if rec.resumes.Load() != 0 {
		t.Errorf("expected no OnResume while renewed, got %d", rec.resumes.Load())
	}
}

// Intent: the lease length follows the live ping interval, not the one in force when the pause began.
func TestPaused_UsesLivePingInterval(t *testing.T) {
	clock := newFakeClock()
	var interval atomic.Int64
	interval.Store(int64(10 * time.Second))
	rec := &hookRecorder{}
	c := New(clock.Now, func() time.Duration { return time.Duration(interval.Load()) }, rec.hooks())

	c.Observe(HostPaused)

	clock.Advance(31 * time.Second)
	if c.Paused() {
		t.Fatal("expected a 10s ping interval to expire the lease after 31s")
	}
	if rec.resumes.Load() != 1 {
		t.Fatalf("expected OnResume once, got %d", rec.resumes.Load())
	}

	// The server lengthens the interval while the host is paused again; the new lease follows it.
	c.Observe(HostPaused)
	interval.Store(int64(60 * time.Second))
	clock.Advance(31 * time.Second)

	if c.Paused() == false {
		t.Error("expected the lease to use the lengthened interval and still hold at 31s")
	}
}

// Intent: a host can be paused again after it resumed, and each entry is its own transition.
func TestObserve_PauseAfterResume_IsANewTransition(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)
	c.Observe(None)
	c.Observe(HostPaused)

	if rec.pauses.Load() != 2 {
		t.Errorf("expected OnPause for each entry into pause, got %d", rec.pauses.Load())
	}
	if rec.resumes.Load() != 1 {
		t.Errorf("expected one OnResume, got %d", rec.resumes.Load())
	}
	if c.Paused() == false {
		t.Error("expected paused again")
	}
}

// Intent: the server switching a paused host's reason (subscription lapses) keeps it paused without
// repeating the clear-the-queue work.
func TestObserve_ReasonChangeWhilePaused_KeepsPauseWithoutNewOnPause(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)
	c.Observe(SubscriptionInactive)

	if c.Paused() == false {
		t.Error("expected to stay paused when the reason changes")
	}
	if rec.pauses.Load() != 1 {
		t.Errorf("expected OnPause only for the transition into pause, got %d", rec.pauses.Load())
	}
}

// Intent: a coordinator built with no hooks works, so callers that do not care about pausing can pass the zero value.
func TestNew_ZeroHooks_AreSafe(t *testing.T) {
	clock := newFakeClock()
	c := New(clock.Now, fixedInterval(60*time.Second), Hooks{})

	c.Observe(HostPaused)
	c.Observe(None)

	if c.Paused() {
		t.Error("expected resumed")
	}
}

// Intent: while the resume reset is still running the host stays gated, so a collector can never run on the
// pre-pause cursor.
func TestResume_HostStaysGatedUntilOnResumeReturns(t *testing.T) {
	clock := newFakeClock()
	started := make(chan struct{})
	release := make(chan struct{})
	c := New(clock.Now, fixedInterval(60*time.Second), Hooks{
		OnResume: func() {
			close(started)
			<-release
		},
	})
	c.Observe(HostPaused)

	done := make(chan struct{})
	go func() {
		defer close(done)
		c.Observe(None)
	}()

	<-started
	if c.Paused() == false {
		t.Error("expected Paused() to be true while OnResume is still running")
	}

	close(release)
	<-done

	if c.Paused() {
		t.Error("expected not paused once OnResume has returned")
	}
}

// Intent: a lease that expires while many collectors poll Paused() runs OnResume exactly once.
func TestPaused_ConcurrentExpiry_ResumesOnce(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	c.Observe(HostPaused)
	clock.Advance(181 * time.Second)

	var wg sync.WaitGroup
	for i := 0; i < 10; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			c.Paused()
		}()
	}
	wg.Wait()

	if rec.resumes.Load() != 1 {
		t.Errorf("expected OnResume exactly once across concurrent callers, got %d", rec.resumes.Load())
	}
	if c.Paused() {
		t.Error("expected resumed once the callers finished")
	}
}

// Intent: Observe and Paused are safe to call from many goroutines at once (run with -race).
func TestCoordinator_ConcurrentObserveAndPaused_NoRace(t *testing.T) {
	clock := newFakeClock()
	rec := &hookRecorder{}
	c := New(clock.Now, fixedInterval(60*time.Second), rec.hooks())

	var wg sync.WaitGroup
	for i := 0; i < 10; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			for j := 0; j < 200; j++ {
				switch (i + j) % 4 {
				case 0:
					c.Observe(HostPaused)
				case 1:
					c.Observe(SubscriptionInactive)
				case 2:
					c.Observe(None)
				default:
					c.Paused()
				}
				clock.Advance(time.Second)
			}
		}(i)
	}
	wg.Wait()

	// Transitions are serialised, so no hook ever runs while another is still executing...
	if rec.overlaps.Load() != 0 {
		t.Errorf("%d hook(s) started while another was still running", rec.overlaps.Load())
	}

	// ...and whatever the interleaving the hooks strictly alternate, starting with a pause.
	rec.mu.Lock()
	defer rec.mu.Unlock()
	for i, event := range rec.events {
		want := "pause"
		if i%2 == 1 {
			want = "resume"
		}
		if event != want {
			t.Fatalf("hook %d was %q, want %q (sequence %v)", i, event, want, rec.events)
		}
	}
}

// Intent: each transition logs once, with the message the operator needs, and a renewal logs nothing.
func TestTransitions_LogOncePerTransition(t *testing.T) {
	clock := newFakeClock()
	var buf bytes.Buffer
	c := New(clock.Now, fixedInterval(60*time.Second), Hooks{})
	c.logger = slog.New(slog.NewTextHandler(&buf, nil))

	c.Observe(HostPaused)
	c.Observe(HostPaused)
	c.Observe(None)
	c.Observe(SubscriptionInactive)
	c.Observe(None)

	out := buf.String()
	cases := []struct {
		message string
		want    int
	}{
		{"host paused by the server: the plan's host limit is reached; telemetry is not being sent", 1},
		{"subscription inactive: telemetry is not being sent", 1},
		{"host resumed by the server: sending telemetry again", 2},
	}
	for _, tc := range cases {
		if got := strings.Count(out, tc.message); got != tc.want {
			t.Errorf("expected %q logged %d time(s), got %d\n%s", tc.message, tc.want, got, out)
		}
	}
}

// Intent: a lease that lapses without a word from the server says so, rather than claiming the server resumed the host.
func TestPaused_LeaseExpiry_LogsExpiryNotServerResume(t *testing.T) {
	clock := newFakeClock()
	var buf bytes.Buffer
	c := New(clock.Now, fixedInterval(60*time.Second), Hooks{})
	c.logger = slog.New(slog.NewTextHandler(&buf, nil))

	c.Observe(HostPaused)
	clock.Advance(181 * time.Second)
	c.Paused()

	out := buf.String()
	if strings.Contains(out, "pause lease expired without a renewing ping: sending telemetry again") == false {
		t.Errorf("expected a lease-expiry log, got:\n%s", out)
	}
	if strings.Contains(out, "host resumed by the server") {
		t.Errorf("a lapsed lease must not be logged as a server resume:\n%s", out)
	}
}

// Intent: the wire enum maps onto the agent's own reasons; anything unknown means "not paused".
func TestFromProto_MapsEveryReason(t *testing.T) {
	cases := []struct {
		in   pb.IngestRejectionReason
		want Reason
	}{
		{pb.IngestRejectionReason_INGEST_REJECTION_REASON_UNSPECIFIED, None},
		{pb.IngestRejectionReason_INGEST_REJECTION_REASON_HOST_PAUSED, HostPaused},
		{pb.IngestRejectionReason_INGEST_REJECTION_REASON_SUBSCRIPTION_INACTIVE, SubscriptionInactive},
		{pb.IngestRejectionReason(99), None},
	}
	for _, tc := range cases {
		if got := FromProto(tc.in); got != tc.want {
			t.Errorf("FromProto(%v) = %v, want %v", tc.in, got, tc.want)
		}
	}
}
