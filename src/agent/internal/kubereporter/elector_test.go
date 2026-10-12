// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"sync"
	"testing"
	"time"

	"github.com/framlux/vord/internal/kube"
)

// electorRig is one fake cluster with a Lease in it and a fake clock that every candidate shares.
type electorRig struct {
	t     *testing.T
	api   *fakeAPI
	clock *fakeClock
}

func newElectorRig(t *testing.T) *electorRig {
	t.Helper()

	api := newFakeAPI(t)
	api.seed(leasePath, leaseManifest(true))

	return &electorRig{t: t, api: api, clock: newFakeClock()}
}

// advance moves the shared clock forward.
func (r *electorRig) advance(d time.Duration) {
	r.clock.Advance(d)
}

// lease returns a copy of the stored Lease, failing the test when it is absent.
func (r *electorRig) lease() map[string]any {
	r.t.Helper()

	lease := r.api.get(leasePath)
	if lease == nil {
		r.t.Fatal("the Lease is absent")
	}

	return lease
}

// holder is the holderIdentity the stored Lease names.
func (r *electorRig) holder() string {
	r.t.Helper()

	return stringAt(r.lease(), "spec", "holderIdentity")
}

// specNumber reads an integer from the stored Lease's spec.
func (r *electorRig) specNumber(key string) int64 {
	r.t.Helper()

	spec, _ := r.lease()["spec"].(map[string]any)

	number, ok := spec[key].(float64)
	if ok == false {
		r.t.Fatalf("spec.%s = %v, want a number", key, spec[key])
	}

	return int64(number)
}

// mutateLease changes the stored Lease as another actor would, which gives it a new resourceVersion.
func (r *electorRig) mutateLease(mutate func(obj map[string]any)) {
	r.t.Helper()

	r.api.mu.Lock()
	defer r.api.mu.Unlock()

	mutateLeaseLocked(r.api, mutate)
}

// mutateLeaseLocked is mutateLease for code that already holds the API lock, such as a fault hook.
func mutateLeaseLocked(api *fakeAPI, mutate func(obj map[string]any)) {
	obj := api.objects[leasePath]
	mutate(obj)
	api.storeLocked(leasePath, obj)
}

// specOf returns the spec of a stored Lease object, creating it if the object has none.
func specOf(obj map[string]any) map[string]any {
	spec, ok := obj["spec"].(map[string]any)
	if ok == false {
		spec = map[string]any{}
		obj["spec"] = spec
	}

	return spec
}

// putCount is how many PUTs of the Lease the API has served.
func (r *electorRig) putCount() int {
	return r.api.requestCount(http.MethodPut, leasePath)
}

// getCount is how many GETs of the Lease the API has served.
func (r *electorRig) getCount() int {
	return r.api.requestCount(http.MethodGet, leasePath)
}

// candidate is one agent's elector, with a gate the test controls.
type candidate struct {
	rig      *electorRig
	identity string
	elector  *Elector
	lost     int

	paused     bool
	ackFresh   bool
	lastAckAt  time.Time
	oldest     func() (time.Time, bool)
	errorSnaps int

	holderWhenLost string
	lostHook       func()
}

// newCandidate builds a candidate for the instance whose client holds a valid reporter token. Its gate starts
// healthy.
func (r *electorRig) newCandidate(instance string) *candidate {
	r.t.Helper()

	token := r.api.issueReporterToken()

	return r.newCandidateWithClient(instance, func() (*kube.Client, error) {
		return kube.New(kube.Config{Server: r.api.url(), CAData: r.api.caPEM, Token: token})
	})
}

func (r *electorRig) newCandidateWithClient(instance string, factory func() (*kube.Client, error)) *candidate {
	r.t.Helper()

	c := &candidate{rig: r, identity: "vord-" + instance, ackFresh: true}

	c.elector = NewElector(factory, c.identity, r.clock, Gate{
		Paused: func() bool { return c.paused },
		LastAck: func() time.Time {
			if c.ackFresh {
				return r.clock.Now()
			}

			return c.lastAckAt
		},
		StartedAt: r.clock.Now(),
		OldestPendingSnapshot: func() (time.Time, bool) {
			if c.oldest == nil {
				return time.Time{}, false
			}

			return c.oldest()
		},
		ConsecutiveErrorSnaps: func() int { return c.errorSnaps },
	}, func() {
		c.lost++

		if lease := r.api.get(leasePath); lease != nil {
			c.holderWhenLost = stringAt(lease, "spec", "holderIdentity")
		}

		if c.lostHook != nil {
			c.lostHook()
		}
	})

	return c
}

func (c *candidate) tick() {
	c.rig.t.Helper()

	c.elector.Tick(context.Background())
}

// requireLeader fails the test unless the candidate is the leader with the given transitions.
func (c *candidate) requireLeader(transitions int64) {
	c.rig.t.Helper()

	got, ok := c.elector.IsLeader()
	if ok == false {
		c.rig.t.Fatalf("%s is not the leader, want it to lead at transitions %d", c.identity, transitions)
	}

	if got != transitions {
		c.rig.t.Fatalf("%s leads at transitions %d, want %d", c.identity, got, transitions)
	}
}

// requireNotLeader fails the test when the candidate reports leadership.
func (c *candidate) requireNotLeader() {
	c.rig.t.Helper()

	if transitions, ok := c.elector.IsLeader(); ok {
		c.rig.t.Fatalf("%s reports leadership at transitions %d, want none", c.identity, transitions)
	}
}

// startedAgo makes the candidate's agent look as if it started d ago.
func (c *candidate) startedAgo(d time.Duration) {
	c.elector.gate.StartedAt = c.rig.clock.Now().Add(-d)
}

// healthy resets the gate to passing.
func (c *candidate) healthy() {
	c.paused = false
	c.ackFresh = true
	c.oldest = nil
	c.errorSnaps = 0
}

// conflictOnce makes the next PUT of the Lease fail with a Conflict after running during, which models another
// actor writing the Lease first.
func conflictOnce(during func(*fakeAPI)) fault {
	return fault{
		method:  http.MethodPut,
		path:    leasePath,
		status:  http.StatusConflict,
		reason:  "Conflict",
		message: "the object has been modified; please apply your changes to the latest version and try again",
		times:   1,
		onFire:  during,
	}
}

// Intent: the first candidate to see a Lease with no holder takes it, recording itself as the holder with the
// duration, times and transition count another candidate will measure against, in the Lease's MicroTime format,
// and writes it as a compare-and-swap on the resourceVersion it read.
func TestElector_AcquiresAnEmptyLease(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	seededVersion := rig.api.resourceVersion(leasePath)

	a.tick()

	a.requireLeader(1)

	lease := rig.lease()
	for field, want := range map[string]string{
		"holderIdentity": "vord-aaaa",
		"acquireTime":    "2026-10-11T12:00:00.000000Z",
		"renewTime":      "2026-10-11T12:00:00.000000Z",
	} {
		if got := stringAt(lease, "spec", field); got != want {
			t.Errorf("spec.%s = %q, want %q", field, got, want)
		}
	}

	if got := rig.specNumber("leaseDurationSeconds"); got != 60 {
		t.Errorf("spec.leaseDurationSeconds = %d, want 60", got)
	}

	if got := rig.specNumber("leaseTransitions"); got != 1 {
		t.Errorf("spec.leaseTransitions = %d, want 1", got)
	}

	var sent map[string]any

	for _, request := range rig.api.requestLog() {
		if (request.Method == http.MethodPut) && (request.Path == leasePath) {
			sent = request.Body
		}
	}

	if got := stringAt(sent, "metadata", "resourceVersion"); got != seededVersion {
		t.Errorf("the PUT carried resourceVersion %q, want the one it read, %q", got, seededVersion)
	}

	if a.lost != 0 {
		t.Errorf("onLost ran %d times while acquiring", a.lost)
	}
}

// Intent: the holder renews on every tick, moving renewTime and nothing else: acquireTime and the transition
// count belong to the acquisition, so a renew must not look like a new one to the server's fence.
func TestElector_RenewsOnEveryTick(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	for i := 1; i <= 3; i++ {
		rig.advance(electionInterval)
		a.tick()

		a.requireLeader(1)

		want := rig.clock.Now().UTC().Format("2006-01-02T15:04:05.000000Z")
		if got := stringAt(rig.lease(), "spec", "renewTime"); got != want {
			t.Errorf("after renew %d spec.renewTime = %q, want %q", i, got, want)
		}

		if got := stringAt(rig.lease(), "spec", "acquireTime"); got != "2026-10-11T12:00:00.000000Z" {
			t.Errorf("after renew %d spec.acquireTime = %q, want the original", i, got)
		}

		if got := rig.specNumber("leaseTransitions"); got != 1 {
			t.Errorf("after renew %d spec.leaseTransitions = %d, want 1", i, got)
		}
	}

	if got := rig.putCount(); got != 4 {
		t.Errorf("PUTs = %d, want 4 (one acquire and three renews)", got)
	}
}

// Intent: an enrol on another host changes the Lease's annotation, which makes a renew fail its compare-and-swap
// even though nothing about the election changed. The holder must read the Lease again, see that it is still the
// holder and renew once more with the new resourceVersion, keeping the annotation the other writer added.
func TestElector_RenewConflictWhoseRereadStillNamesUs_RetriesAndKeepsTheLease(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)
	rig.api.inject(conflictOnce(func(api *fakeAPI) {
		mutateLeaseLocked(api, func(obj map[string]any) {
			metadata := obj["metadata"].(map[string]any)
			metadata["annotations"] = map[string]any{roleVersionAnnotation: "1", "example.com/added-by": "another-writer"}
		})
	}))

	putsBefore := rig.putCount()
	a.tick()

	a.requireLeader(1)

	if got := rig.putCount() - putsBefore; got != 2 {
		t.Errorf("renew PUTs = %d, want 2 (the conflict and the retry)", got)
	}

	if got := stringAt(rig.lease(), "spec", "renewTime"); got != rig.clock.Now().UTC().Format("2006-01-02T15:04:05.000000Z") {
		t.Errorf("spec.renewTime = %q, the retried renew did not land", got)
	}

	if got := stringAt(rig.lease(), "metadata", "annotations", "example.com/added-by"); got != "another-writer" {
		t.Errorf("the other writer's annotation = %q, want it kept", got)
	}

	if a.lost != 0 {
		t.Errorf("onLost ran %d times, want 0", a.lost)
	}
}

// Intent: when the renew's re-read shows another holder, this agent no longer holds the Lease. It steps down at
// once, tells the queue, and writes nothing more, instead of overwriting the new holder.
func TestElector_RenewConflictWhoseRereadNamesAnotherHolder_StepsDown(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)
	rig.api.inject(conflictOnce(func(api *fakeAPI) {
		mutateLeaseLocked(api, func(obj map[string]any) {
			specOf(obj)["holderIdentity"] = "vord-bbbb"
		})
	}))

	putsBefore := rig.putCount()
	a.tick()

	a.requireNotLeader()

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", a.lost)
	}

	if got := rig.putCount() - putsBefore; got != 1 {
		t.Errorf("PUTs = %d, want only the one that conflicted", got)
	}

	if got := rig.holder(); got != "vord-bbbb" {
		t.Errorf("holder = %q, want the other agent's claim left alone", got)
	}
}

// Intent: a retry that conflicts again is not evidence of losing the Lease. The holder keeps it, makes no third
// attempt this tick, and leaves the renew deadline to decide.
func TestElector_RenewConflictsTwice_KeepsLeadershipWithoutAThirdAttempt(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)

	touch := func(api *fakeAPI) {
		mutateLeaseLocked(api, func(obj map[string]any) {
			obj["metadata"].(map[string]any)["labels"].(map[string]any)["touched"] = "yes"
		})
	}

	rig.api.inject(conflictOnce(touch))
	rig.api.inject(conflictOnce(touch))

	putsBefore := rig.putCount()
	a.tick()

	a.requireLeader(1)

	if got := rig.putCount() - putsBefore; got != 2 {
		t.Errorf("PUTs = %d, want 2 (the renew and its single retry)", got)
	}

	if a.lost != 0 {
		t.Errorf("onLost ran %d times, want 0", a.lost)
	}
}

// Intent: a holder that reads the Lease and finds another agent named on it (for instance after a stall long
// enough for a standby to take over) is no longer the holder. It stops leading at once and writes nothing, rather
// than overwriting the new holder's claim.
func TestElector_HolderThatReadsAnotherHolder_StepsDownWithoutWriting(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.mutateLease(func(obj map[string]any) {
		spec := specOf(obj)
		spec["holderIdentity"] = "vord-bbbb"
		spec["leaseTransitions"] = float64(2)
	})

	rig.advance(electionInterval)

	putsBefore := rig.putCount()
	a.tick()

	a.requireNotLeader()

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", a.lost)
	}

	if got := rig.putCount() - putsBefore; got != 0 {
		t.Errorf("PUTs = %d, want none: the new holder's claim is fresh", got)
	}

	if got := rig.holder(); got != "vord-bbbb" {
		t.Errorf("holder = %q, want the new holder undisturbed", got)
	}
}

// Intent: two candidates that read the same empty Lease race on the compare-and-swap, and the loser must read
// again, learn who won, and not believe it holds the Lease.
func TestElector_AcquireConflict_RereadsAndDoesNotHold(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")

	rig.api.inject(conflictOnce(func(api *fakeAPI) {
		mutateLeaseLocked(api, func(obj map[string]any) {
			spec := specOf(obj)
			spec["holderIdentity"] = "vord-bbbb"
			spec["leaseTransitions"] = float64(1)
		})
	}))

	a.tick()

	a.requireNotLeader()

	if got := rig.getCount(); got != 2 {
		t.Errorf("GETs = %d, want 2 (the read and the re-read)", got)
	}

	if got := rig.putCount(); got != 1 {
		t.Errorf("PUTs = %d, want 1: the loser must not try again", got)
	}

	if got := rig.holder(); got != "vord-bbbb" {
		t.Errorf("holder = %q, want the winner", got)
	}

	if a.lost != 0 {
		t.Errorf("onLost ran %d times for a Lease that was never held", a.lost)
	}

	// The re-read counts as the first sight of the winner's record, so the loser waits the full duration.
	rig.advance(electionInterval)
	a.tick()

	a.requireNotLeader()

	if got := rig.putCount(); got != 1 {
		t.Errorf("PUTs = %d, want no contention while the winner's record is fresh", got)
	}
}

// gateFailureCase is one way for a rule of the health gate to fail, and the setup that causes it.
type gateFailureCase struct {
	name  string
	apply func(c *candidate)
}

// gateFailureCases lists one failure for each rule of the health gate. None of them moves the clock, so a holder
// that is renewing every tick is still within its renew deadline when its gate fails.
func gateFailureCases() []gateFailureCase {
	return []gateFailureCase{
		{"host is paused", func(c *candidate) { c.paused = true }},
		{"no ack since the agent started", func(c *candidate) {
			c.ackFresh = false
			c.lastAckAt = time.Time{}
			c.startedAgo(5 * time.Minute)
		}},
		{"last ack is five minutes old", func(c *candidate) {
			c.ackFresh = false
			c.lastAckAt = c.rig.clock.Now().Add(-5 * time.Minute)
			c.startedAgo(time.Hour)
		}},
		{"oldest pending snapshot is fifteen minutes old", func(c *candidate) {
			c.oldest = func() (time.Time, bool) { return c.rig.clock.Now().Add(-15 * time.Minute), true }
		}},
		{"three consecutive error snapshots", func(c *candidate) { c.errorSnaps = 3 }},
	}
}

// Intent: a candidate whose gate fails never contends for the Lease, so a broken host cannot take it from healthy
// ones. It must make no request at all, and contend as soon as the gate passes again.
func TestElector_FailingGate_PreventsAcquiring(t *testing.T) {
	for _, tc := range gateFailureCases() {
		t.Run(tc.name, func(t *testing.T) {
			rig := newElectorRig(t)
			a := rig.newCandidate("aaaa")

			tc.apply(a)
			a.tick()

			a.requireNotLeader()

			if got := len(rig.api.requestLog()); got != 0 {
				t.Errorf("a failing candidate made %d requests, want none", got)
			}

			if got := rig.holder(); got != "" {
				t.Errorf("holder = %q, want the Lease left empty", got)
			}

			a.healthy()
			a.tick()

			a.requireLeader(1)
		})
	}
}

// Intent: the gate's thresholds are exact, so a host one second inside a limit keeps its place and one on the
// limit does not.
func TestElector_GateThresholds(t *testing.T) {
	rig := newElectorRig(t)
	rig.advance(time.Hour)

	now := rig.clock.Now()
	a := rig.newCandidate("aaaa")

	cases := []struct {
		name    string
		arrange func()
		fails   bool
	}{
		{"ack 4m59s old", func() {
			a.ackFresh = false
			a.lastAckAt = now.Add(-4*time.Minute - 59*time.Second)
			a.elector.gate.StartedAt = now.Add(-time.Hour)
		}, false},
		{"ack 5m old", func() {
			a.ackFresh = false
			a.lastAckAt = now.Add(-5 * time.Minute)
			a.elector.gate.StartedAt = now.Add(-time.Hour)
		}, true},
		{"no ack, started 4m59s ago", func() {
			a.ackFresh = false
			a.lastAckAt = time.Time{}
			a.elector.gate.StartedAt = now.Add(-4*time.Minute - 59*time.Second)
		}, false},
		{"no ack, started 5m ago", func() {
			a.ackFresh = false
			a.lastAckAt = time.Time{}
			a.elector.gate.StartedAt = now.Add(-5 * time.Minute)
		}, true},
		{"old ack, started 1m ago", func() {
			a.ackFresh = false
			a.lastAckAt = now.Add(-time.Hour)
			a.elector.gate.StartedAt = now.Add(-time.Minute)
		}, false},
		{"snapshot 14m59s old", func() { a.oldest = func() (time.Time, bool) { return now.Add(-14*time.Minute - 59*time.Second), true } }, false},
		{"snapshot 15m old", func() { a.oldest = func() (time.Time, bool) { return now.Add(-15 * time.Minute), true } }, true},
		{"no pending snapshot", func() { a.oldest = func() (time.Time, bool) { return now.Add(-24 * time.Hour), false } }, false},
		{"two error snapshots", func() { a.errorSnaps = 2 }, false},
		{"three error snapshots", func() { a.errorSnaps = 3 }, true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			a.healthy()
			tc.arrange()

			reason := a.elector.gateFailure(now)
			if (reason != "") != tc.fails {
				t.Errorf("gateFailure = %q, fails = %v, want fails = %v", reason, reason != "", tc.fails)
			}
		})
	}
}

// Intent: a holder whose gate fails hands the Lease back instead of hogging it: it clears the holder (so a
// healthy standby can take over at once), tells the queue, and then stays out for a full lease duration, so a
// host that cannot deliver does not win the Lease straight back.
func TestElector_GateFailureWhileHolding_ReleasesNotifiesAndBacksOff(t *testing.T) {
	for _, tc := range gateFailureCases() {
		t.Run(tc.name, func(t *testing.T) {
			rig := newElectorRig(t)
			a := rig.newCandidate("aaaa")
			a.tick()
			a.requireLeader(1)

			rig.advance(electionInterval)
			tc.apply(a)

			putsBefore := rig.putCount()
			a.tick()

			a.requireNotLeader()

			if got := rig.putCount() - putsBefore; got != 1 {
				t.Errorf("PUTs on the failing tick = %d, want only the release", got)
			}

			if got := rig.holder(); got != "" {
				t.Errorf("holder = %q after the release, want it cleared", got)
			}

			if a.lost != 1 {
				t.Errorf("onLost ran %d times, want 1", a.lost)
			}

			if a.holderWhenLost != "" {
				t.Errorf("onLost ran while the Lease still named %q, want the release first", a.holderWhenLost)
			}

			if got := rig.specNumber("leaseTransitions"); got != 1 {
				t.Errorf("spec.leaseTransitions = %d after a release, want it kept at 1", got)
			}

			// Healthy again, but the back-off has 60 s to run from the release.
			a.healthy()

			requestsAtRelease := len(rig.api.requestLog())

			rig.advance(40 * time.Second)
			a.tick()
			rig.advance(19 * time.Second)
			a.tick()

			a.requireNotLeader()

			if got := len(rig.api.requestLog()); got != requestsAtRelease {
				t.Errorf("a candidate in back-off made %d requests, want none", got-requestsAtRelease)
			}

			rig.advance(time.Second)
			a.tick()

			a.requireLeader(2)

			if a.lost != 1 {
				t.Errorf("onLost ran %d times in total, want 1", a.lost)
			}
		})
	}
}

// Intent: the release is the hand-over: a healthy standby that was watching must be able to take the Lease on its
// very next tick, without waiting for it to expire.
func TestElector_GateFailureWhileHolding_LetsAHealthyStandbyTakeOverAtOnce(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	a.tick()
	b.tick()

	b.requireNotLeader()

	rig.advance(electionInterval)
	a.paused = true
	a.tick()

	rig.advance(electionInterval)
	b.tick()

	b.requireLeader(2)

	if got := rig.holder(); got != "vord-bbbb" {
		t.Errorf("holder = %q, want the standby", got)
	}
}

// Intent: a release that cannot reach the API still ends this agent's claim as far as it is concerned: it must
// stop acting as leader, drop its snapshots and back off, rather than keep leading on a Lease it was told to give
// up.
func TestElector_GateFailureWhileHolding_ReleaseFailureStillStepsDownAndBacksOff(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)
	rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: -1})
	a.paused = true
	a.tick()

	a.requireNotLeader()

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", a.lost)
	}

	a.healthy()

	requests := len(rig.api.requestLog())

	rig.advance(electionInterval)
	a.tick()

	if got := len(rig.api.requestLog()); got != requests {
		t.Errorf("made %d requests during the back-off, want none", got-requests)
	}
}

// Intent: the renew deadline is measured from the moment before the PUT that renewed, because that is the last
// instant this agent knows it held the Lease. A slow request must therefore shorten the deadline, not extend it.
func TestElector_RenewDeadline_IsMeasuredFromBeforeTheRenewPut(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)
	rig.api.inject(fault{
		method: http.MethodPut,
		path:   leasePath,
		times:  1,
		onFire: func(*fakeAPI) { rig.clock.Advance(39 * time.Second) },
	})
	a.tick()

	// The renew was sent at +20 s and the answer came back at +59 s.
	a.requireLeader(1)

	rig.advance(time.Second)
	a.requireLeader(1)

	rig.advance(time.Second)
	a.requireNotLeader()

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want 1", a.lost)
	}
}

// Intent: a holder that cannot renew stops acting as leader the moment the renew deadline passes, not at its next
// tick, and tells the queue exactly once however many times it is asked afterwards.
func TestElector_RenewDeadlinePassing_MakesIsLeaderFalseAtOnceAndFiresOnLostOnce(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: -1})

	rig.advance(electionInterval)
	a.tick()

	a.requireLeader(1)

	rig.advance(electionInterval)
	a.requireLeader(1)

	rig.advance(time.Second)
	a.requireNotLeader()

	if a.lost != 1 {
		t.Fatalf("onLost ran %d times when the deadline passed, want 1", a.lost)
	}

	a.requireNotLeader()
	a.tick()

	if a.lost != 1 {
		t.Errorf("onLost ran %d times after a tick, want it fired once", a.lost)
	}
}

// Intent: losing the Lease to a stalled API is not the same as losing it to another agent. If the record still
// names this agent when the API comes back, it renews that claim in place, so the transition count only moves when
// the holder really changed.
func TestElector_AfterTheDeadlineAndNobodyElse_RenewsWithoutANewTransition(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: 1})

	rig.advance(electionInterval)
	a.tick()

	rig.advance(21 * time.Second)
	a.requireNotLeader()

	if a.lost != 1 {
		t.Fatalf("onLost ran %d times, want 1", a.lost)
	}

	a.tick()

	a.requireLeader(1)

	if got := rig.specNumber("leaseTransitions"); got != 1 {
		t.Errorf("spec.leaseTransitions = %d, want 1", got)
	}

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want it fired once", a.lost)
	}
}

// Intent: the same holds for an agent restart. The new process has the same identity, finds the record naming it,
// and takes the claim over in place.
func TestElector_RestartedAgentWithTheSameIdentity_RenewsItsOwnClaim(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	restarted := rig.newCandidate("aaaa")
	restarted.requireNotLeader()

	rig.advance(electionInterval)
	restarted.tick()

	restarted.requireLeader(1)

	if got := rig.specNumber("leaseTransitions"); got != 1 {
		t.Errorf("spec.leaseTransitions = %d, want 1", got)
	}
}

// Intent: a failed read leaves leadership to the renew deadline instead of dropping it on the first hiccup, and
// makes no write on a stale view.
func TestElector_ReadFailureWhileHolding_KeepsLeadershipUntilTheDeadline(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.api.inject(fault{method: http.MethodGet, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: 1})

	rig.advance(electionInterval)

	putsBefore := rig.putCount()
	a.tick()

	a.requireLeader(1)

	if got := rig.putCount() - putsBefore; got != 0 {
		t.Errorf("PUTs after a failed read = %d, want none", got)
	}

	if a.lost != 0 {
		t.Errorf("onLost ran %d times, want 0", a.lost)
	}
}

// expiryCases are the records a standby can find a live holder's Lease in, including ones whose own timestamps
// lie about their age.
func expiryCases() map[string]string {
	return map[string]string{
		"renewTime an hour in the future": "2026-10-11T13:00:00.000000Z",
		"renewTime an hour in the past":   "2026-10-11T11:00:00.000000Z",
		"renewTime without fractions":     "2026-10-11T12:00:00Z",
		"renewTime missing":               "",
	}
}

// Intent: a standby decides a holder has expired by watching its record stand still on its own clock, never by
// reading the holder's timestamps, which another node's skewed clock can make arbitrarily wrong either way.
func TestElector_ExpiryIsMeasuredLocally_WhateverTheRecordSays(t *testing.T) {
	for name, renewTime := range expiryCases() {
		t.Run(name, func(t *testing.T) {
			rig := newElectorRig(t)
			a := rig.newCandidate("aaaa")
			a.tick()

			rig.mutateLease(func(obj map[string]any) {
				spec := specOf(obj)
				if renewTime == "" {
					delete(spec, "renewTime")
				} else {
					spec["renewTime"] = renewTime
				}
			})

			b := rig.newCandidate("bbbb")
			b.tick()

			b.requireNotLeader()

			putsBefore := rig.putCount()

			rig.advance(59 * time.Second)
			b.tick()

			b.requireNotLeader()

			if got := rig.putCount() - putsBefore; got != 0 {
				t.Errorf("PUTs 59 s after first seeing the record = %d, want none", got)
			}

			rig.advance(time.Second)
			b.tick()

			b.requireLeader(2)

			if got := rig.holder(); got != "vord-bbbb" {
				t.Errorf("holder = %q, want the standby after the record stood still for 60 s", got)
			}
		})
	}
}

// Intent: every renew by the holder changes the record, and a standby must start its 60 s again from each change,
// or a healthy holder would be taken over from just because the standby first looked a minute ago.
func TestElector_AChangingRecordRestartsTheStandbysClock(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	a.tick()
	b.tick()

	rig.advance(electionInterval)
	a.tick()
	b.tick()

	// The holder falls silent after the renew at +20 s.
	rig.advance(59 * time.Second)
	b.tick()

	b.requireNotLeader()

	rig.advance(time.Second)
	b.tick()

	b.requireLeader(2)
}

// Intent: the transition count is the fence the server orders snapshots by, so it must rise by one on each new
// acquisition, whoever makes it and however the previous holder ended, and stay put on renews.
func TestElector_TransitionsIncrementOnEachNewAcquisition(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	a.tick()
	a.requireLeader(1)

	rig.advance(electionInterval)
	a.tick()
	a.requireLeader(1)

	a.elector.Release(context.Background())

	b.tick()
	b.requireLeader(2)

	rig.advance(electionInterval)
	b.tick()
	b.requireLeader(2)

	b.elector.Release(context.Background())

	rig.advance(releaseBackoff)
	a.tick()
	a.requireLeader(3)

	if got := rig.specNumber("leaseTransitions"); got != 3 {
		t.Errorf("spec.leaseTransitions = %d, want 3", got)
	}

	// A takeover from a holder that simply vanished counts too.
	rig.advance(leaseDuration)
	b.tick()
	rig.advance(leaseDuration)
	b.tick()

	b.requireLeader(4)
}

// Intent: only the holder leads. A standby, watching a healthy holder renew, never reports leadership and never
// writes the Lease, so only one agent ever collects.
func TestElector_AStandbyNeverReportsLeadershipOrWrites(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	a.tick()

	for i := 0; i < 5; i++ {
		putsBefore := rig.putCount()

		b.tick()
		b.requireNotLeader()

		if got := rig.putCount() - putsBefore; got != 0 {
			t.Fatalf("the standby wrote the Lease %d times", got)
		}

		rig.advance(electionInterval)
		a.tick()
		a.requireLeader(1)
	}

	if b.lost != 0 {
		t.Errorf("a standby's onLost ran %d times, want 0", b.lost)
	}
}

// Intent: handing the Lease back clears only the holder, so the transition count survives, and it tells the
// queue; an agent that is not the holder must not touch a Lease that belongs to someone else.
func TestElector_ReleaseClearsTheHolderOnlyWhenItIsTheHolder(t *testing.T) {
	t.Run("holder", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		a.tick()

		rig.advance(electionInterval)
		a.elector.Release(context.Background())

		if got := rig.holder(); got != "" {
			t.Errorf("holder = %q, want it cleared", got)
		}

		if got := rig.specNumber("leaseTransitions"); got != 1 {
			t.Errorf("spec.leaseTransitions = %d, want 1", got)
		}

		a.requireNotLeader()

		if a.lost != 1 {
			t.Errorf("onLost ran %d times, want 1", a.lost)
		}

		if a.holderWhenLost != "" {
			t.Errorf("onLost ran while the Lease still named %q", a.holderWhenLost)
		}
	})

	t.Run("not the holder", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		b := rig.newCandidate("bbbb")
		a.tick()

		versionBefore := rig.api.resourceVersion(leasePath)

		b.elector.Release(context.Background())

		if got := rig.holder(); got != "vord-aaaa" {
			t.Errorf("holder = %q, want the real holder undisturbed", got)
		}

		if got := rig.api.resourceVersion(leasePath); got != versionBefore {
			t.Errorf("the Lease was written (resourceVersion %q, was %q)", got, versionBefore)
		}

		if b.lost != 0 {
			t.Errorf("onLost ran %d times for an agent that held nothing", b.lost)
		}
	})

	t.Run("conflict is retried", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		a.tick()

		rig.api.inject(conflictOnce(func(api *fakeAPI) {
			mutateLeaseLocked(api, func(obj map[string]any) {
				obj["metadata"].(map[string]any)["labels"].(map[string]any)["touched"] = "yes"
			})
		}))

		a.elector.Release(context.Background())

		if got := rig.holder(); got != "" {
			t.Errorf("holder = %q, want it cleared despite the conflict", got)
		}

		if got := stringAt(rig.lease(), "metadata", "labels", "touched"); got != "yes" {
			t.Errorf("the other writer's label = %q, want it kept", got)
		}
	})

	t.Run("no Lease", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		a.tick()

		rig.api.mu.Lock()
		rig.api.deleteLocked(leasePath)
		rig.api.mu.Unlock()

		a.elector.Release(context.Background())

		a.requireNotLeader()

		if a.lost != 1 {
			t.Errorf("onLost ran %d times, want 1", a.lost)
		}
	})
}

// Intent: the server fences per Lease epoch, so the elector must report the identity of the Lease it last read,
// including after the Lease was deleted and created again by a re-enable.
func TestElector_LeaseEpochFollowsTheLeaseLastObserved(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")

	if uid, created := a.elector.LeaseEpoch(); (uid != "") || (created != "") {
		t.Fatalf("LeaseEpoch before any read = (%q, %q), want empty", uid, created)
	}

	a.tick()

	uid, created := a.elector.LeaseEpoch()
	if (uid != stringAt(rig.lease(), "metadata", "uid")) || (uid == "") || (created != "2026-10-11T12:00:00Z") {
		t.Errorf("LeaseEpoch = (%q, %q), want the stored Lease's uid and creationTimestamp", uid, created)
	}

	firstUID := uid

	rig.api.mu.Lock()
	rig.api.deleteLocked(leasePath)
	rig.api.mu.Unlock()
	rig.api.seed(leasePath, leaseManifest(true))

	rig.advance(electionInterval)
	a.tick()

	uid, _ = a.elector.LeaseEpoch()
	if uid == firstUID {
		t.Errorf("LeaseEpoch uid = %q after the Lease was recreated, want a new one", uid)
	}

	// The new Lease started its count again, and this agent, finding it empty, acquired it afresh.
	a.requireLeader(1)

	if a.lost != 1 {
		t.Errorf("onLost ran %d times, want 1 for the Lease that was replaced", a.lost)
	}
}

// Intent: the Lease is read-modify-written as raw JSON so that fields this agent does not know, from a newer
// Kubernetes or another tool, survive every acquire, renew and release.
func TestElector_PreservesFieldsItDoesNotKnow(t *testing.T) {
	rig := newElectorRig(t)
	rig.mutateLease(func(obj map[string]any) {
		metadata := obj["metadata"].(map[string]any)
		metadata["annotations"] = map[string]any{roleVersionAnnotation: "1", "example.com/note": "keep me"}
		spec := specOf(obj)
		spec["strategy"] = "OldestEmulationVersion"
		spec["preferredHolder"] = "vord-zzzz"
	})

	a := rig.newCandidate("aaaa")
	a.tick()

	rig.advance(electionInterval)
	a.tick()

	a.elector.Release(context.Background())

	lease := rig.lease()
	if got := stringAt(lease, "spec", "strategy"); got != "OldestEmulationVersion" {
		t.Errorf("spec.strategy = %q, want it kept", got)
	}

	if got := stringAt(lease, "spec", "preferredHolder"); got != "vord-zzzz" {
		t.Errorf("spec.preferredHolder = %q, want it kept", got)
	}

	if got := stringAt(lease, "metadata", "annotations", "example.com/note"); got != "keep me" {
		t.Errorf("annotation = %q, want it kept", got)
	}

	if got := stringAt(lease, "metadata", "annotations", roleVersionAnnotation); got != "1" {
		t.Errorf("role-version annotation = %q, want it kept", got)
	}

	if got := stringAt(lease, "metadata", "labels", managedByLabel); got != managedByValue {
		t.Errorf("managed-by label = %q, want it kept", got)
	}
}

// Intent: a token the cluster refuses is reported as credentials_rejected, from any Lease call, without being a
// reason to give up the Lease: the holder cannot write either, so it loses the Lease on the renew deadline like
// any holder that cannot renew. A successful call clears the report.
func TestElector_AuthRejection_IsReportedAndClearedButNeverReleases(t *testing.T) {
	t.Run("401 on the read", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidateWithClient("aaaa", func() (*kube.Client, error) {
			return kube.New(kube.Config{Server: rig.api.url(), CAData: rig.api.caPEM, Token: "a-token-the-cluster-never-issued"})
		})

		a.tick()

		if got := a.elector.ReporterError(); got != "credentials_rejected" {
			t.Errorf("ReporterError = %q, want credentials_rejected", got)
		}

		a.requireNotLeader()
	})

	t.Run("403 on the write", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusForbidden, reason: "Forbidden", message: "leases is forbidden", times: 1})

		a.tick()

		if got := a.elector.ReporterError(); got != "credentials_rejected" {
			t.Errorf("ReporterError = %q, want credentials_rejected", got)
		}
	})

	t.Run("a holder keeps the Lease and recovers", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		a.tick()

		rig.api.inject(fault{method: http.MethodGet, path: leasePath, status: http.StatusUnauthorized, reason: "Unauthorized", message: "Unauthorized", times: 1})

		rig.advance(electionInterval)
		a.tick()

		if got := a.elector.ReporterError(); got != "credentials_rejected" {
			t.Fatalf("ReporterError = %q, want credentials_rejected", got)
		}

		a.requireLeader(1)

		if a.lost != 0 {
			t.Errorf("onLost ran %d times: a rejected credential is not a gate failure", a.lost)
		}

		if got := rig.holder(); got != "vord-aaaa" {
			t.Errorf("holder = %q, want the Lease left alone", got)
		}

		rig.advance(electionInterval)
		a.tick()

		if got := a.elector.ReporterError(); got != "" {
			t.Errorf("ReporterError = %q after a successful call, want it cleared", got)
		}
	})

	t.Run("a namespace being deleted is not a rejected credential", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		rig.api.inject(fault{
			method: http.MethodGet, path: leasePath, status: http.StatusForbidden, reason: "Forbidden",
			message: "unable to modify content in namespace vord-system because it is being terminated", causes: []string{"NamespaceTerminating"}, times: 1,
		})

		a.tick()

		if got := a.elector.ReporterError(); got != "" {
			t.Errorf("ReporterError = %q, want none", got)
		}
	})
}

// Intent: a cluster whose CA this agent no longer trusts is reported as tls_ca_changed, because that is the one
// error the user can only fix by re-enrolling, and a later success clears it.
func TestElector_TLSVerificationFailure_IsReportedAsCAChanged(t *testing.T) {
	rig := newElectorRig(t)
	token := rig.api.issueReporterToken()
	trusted := true

	a := rig.newCandidateWithClient("aaaa", func() (*kube.Client, error) {
		ca := rig.api.caPEM
		if trusted == false {
			ca = unrelatedCAPEM(t)
		}

		return kube.New(kube.Config{Server: rig.api.url(), CAData: ca, Token: token})
	})

	trusted = false

	a.tick()

	if got := a.elector.ReporterError(); got != "tls_ca_changed" {
		t.Errorf("ReporterError = %q, want tls_ca_changed", got)
	}

	a.requireNotLeader()
}

// Intent: only a rejected credential or an unverifiable certificate is reported. An unreachable API, a server
// error or a missing Lease says nothing about the credentials, so it neither sets the report nor clears one that
// is still true.
func TestElector_OtherFailuresNeitherSetNorClearTheReport(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")

	for _, status := range []int{http.StatusInternalServerError, http.StatusBadGateway, http.StatusNotFound} {
		rig.api.inject(fault{method: http.MethodGet, path: leasePath, status: status, reason: "Failure", times: 1})
		a.tick()

		if got := a.elector.ReporterError(); got != "" {
			t.Errorf("ReporterError = %q after a %d, want none", got, status)
		}
	}

	rig.api.inject(fault{method: http.MethodGet, path: leasePath, status: http.StatusUnauthorized, reason: "Unauthorized", times: 1})
	a.tick()

	for _, status := range []int{http.StatusInternalServerError, http.StatusNotFound} {
		rig.api.inject(fault{method: http.MethodGet, path: leasePath, status: status, reason: "Failure", times: 1})
		a.tick()

		if got := a.elector.ReporterError(); got != "credentials_rejected" {
			t.Errorf("ReporterError = %q after a %d, want the earlier report kept", got, status)
		}
	}
}

// Intent: a Lease that has been deleted (the cluster was disabled or uninstalled) cannot be renewed, so a holder
// must stop leading and drop its snapshots at once instead of sending for a cluster that is no longer reporting;
// a standby simply waits.
func TestElector_MissingLease(t *testing.T) {
	t.Run("holder steps down", func(t *testing.T) {
		rig := newElectorRig(t)
		a := rig.newCandidate("aaaa")
		a.tick()

		rig.api.mu.Lock()
		rig.api.deleteLocked(leasePath)
		rig.api.mu.Unlock()

		rig.advance(electionInterval)
		a.tick()

		a.requireNotLeader()

		if a.lost != 1 {
			t.Errorf("onLost ran %d times, want 1", a.lost)
		}
	})

	t.Run("standby waits quietly", func(t *testing.T) {
		rig := newElectorRig(t)
		rig.api.mu.Lock()
		rig.api.deleteLocked(leasePath)
		rig.api.mu.Unlock()

		a := rig.newCandidate("aaaa")
		a.tick()

		a.requireNotLeader()

		if got := a.elector.ReporterError(); got != "" {
			t.Errorf("ReporterError = %q, want none for a missing Lease", got)
		}

		if got := rig.putCount(); got != 0 {
			t.Errorf("PUTs = %d, want none: the reporter cannot create the Lease", got)
		}
	})
}

// Intent: a client that cannot be built this tick (for example while the state is being rewritten) is retried on
// the next one, and once built it is reused rather than rebuilt on every tick.
func TestElector_ClientFactory_IsRetriedAfterAFailureAndThenReused(t *testing.T) {
	rig := newElectorRig(t)
	token := rig.api.issueReporterToken()
	calls := 0

	a := rig.newCandidateWithClient("aaaa", func() (*kube.Client, error) {
		calls++
		if calls == 1 {
			return nil, errors.New("the state is being rewritten")
		}

		return kube.New(kube.Config{Server: rig.api.url(), CAData: rig.api.caPEM, Token: token})
	})

	a.tick()

	a.requireNotLeader()

	if got := len(rig.api.requestLog()); got != 0 {
		t.Errorf("requests with no client = %d, want none", got)
	}

	rig.advance(electionInterval)
	a.tick()

	a.requireLeader(1)

	rig.advance(electionInterval)
	a.tick()

	if calls != 2 {
		t.Errorf("the factory ran %d times, want 2 (the failure and the one success)", calls)
	}
}

// Intent: every Lease request has its own 10 s limit, shorter than the 20 s tick, so one hung request cannot make
// a tick run into the next or hold the renew deadline hostage.
func TestElector_EveryLeaseRequestHasATenSecondTimeout(t *testing.T) {
	if leaseRequestTimeout != 10*time.Second {
		t.Errorf("leaseRequestTimeout = %v, want 10s", leaseRequestTimeout)
	}

	if leaseRequestTimeout >= electionInterval {
		t.Errorf("leaseRequestTimeout %v is not shorter than the %v election interval", leaseRequestTimeout, electionInterval)
	}

	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")

	var limits []time.Duration

	a.elector.withTimeout = func(ctx context.Context, d time.Duration) (context.Context, context.CancelFunc) {
		limits = append(limits, d)

		return context.WithTimeout(ctx, d)
	}

	a.tick()
	a.elector.Release(context.Background())

	requests := len(rig.api.requestLog())
	if requests != 4 {
		t.Fatalf("requests = %d, want 4 (read and write to acquire, read and write to release)", requests)
	}

	if len(limits) != requests {
		t.Fatalf("limits applied = %d for %d requests, want one each", len(limits), requests)
	}

	for i, limit := range limits {
		if limit != leaseRequestTimeout {
			t.Errorf("request %d ran under a %v limit, want %v", i, limit, leaseRequestTimeout)
		}
	}
}

// Intent: a protocol constant is part of the contract with the cluster and with the other agents, so its value is
// pinned rather than merely used.
func TestElector_ProtocolConstants(t *testing.T) {
	for name, pair := range map[string][2]time.Duration{
		"election interval": {electionInterval, 20 * time.Second},
		"lease duration":    {leaseDuration, 60 * time.Second},
		"renew deadline":    {renewDeadline, 40 * time.Second},
		"release back-off":  {releaseBackoff, 60 * time.Second},
		"ack age limit":     {gateMaxAckAge, 5 * time.Minute},
		"snapshot age":      {gateMaxSnapshotAge, 15 * time.Minute},
	} {
		if pair[0] != pair[1] {
			t.Errorf("%s = %v, want %v", name, pair[0], pair[1])
		}
	}

	if gateMaxErrorSnapshots != 3 {
		t.Errorf("gateMaxErrorSnapshots = %d, want 3", gateMaxErrorSnapshots)
	}
}

// Intent: the Lease's MicroTime is RFC 3339 in UTC with exactly six fractional digits, whatever zone the clock is
// in and even when the microseconds are zero, because the API server rejects any other shape.
func TestFormatMicroTime(t *testing.T) {
	zone := time.FixedZone("plus-one", 3600)

	cases := map[string]struct {
		at   time.Time
		want string
	}{
		"whole second":     {time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC), "2026-10-11T12:00:00.000000Z"},
		"microseconds":     {time.Date(2026, 10, 11, 12, 0, 0, 500123000, time.UTC), "2026-10-11T12:00:00.500123Z"},
		"nanoseconds drop": {time.Date(2026, 10, 11, 12, 0, 0, 456_789_999, time.UTC), "2026-10-11T12:00:00.456789Z"},
		"other zone":       {time.Date(2026, 10, 11, 13, 0, 0, 7000, zone), "2026-10-11T12:00:00.000007Z"},
	}

	for name, tc := range cases {
		if got := formatMicroTime(tc.at); got != tc.want {
			t.Errorf("%s: formatMicroTime = %q, want %q", name, got, tc.want)
		}

		if _, err := time.Parse(time.RFC3339Nano, tc.want); err != nil {
			t.Errorf("%s: %q is not RFC 3339: %v", name, tc.want, err)
		}
	}
}

// Intent: the snapshot loop reads IsLeader while the election loop ticks, so the elector's state must be safe to
// share. The race detector fails this test if it is not.
func TestElector_IsLeaderIsSafeWhileTicking(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	stop := make(chan struct{})

	var readers sync.WaitGroup

	for _, c := range []*candidate{a, b} {
		readers.Add(1)

		go func() {
			defer readers.Done()

			for {
				select {
				case <-stop:
					return
				default:
				}

				c.elector.IsLeader()
				c.elector.LeaseEpoch()
				c.elector.ReporterError()
			}
		}()
	}

	for i := 0; i < 20; i++ {
		a.tick()
		b.tick()
		rig.advance(electionInterval)
	}

	close(stop)
	readers.Wait()

	a.requireLeader(1)
}

// Intent: a holder told to give the Lease up must stop leading before the cluster is told, or there is a moment
// when the Lease is free for a standby to take while this agent still reports that it leads and may enqueue a
// snapshot. Whatever the route to the release, this agent must already be a non-leader when the clearing write
// reaches the API.
func TestElector_Release_StopsLeadingBeforeTheLeaseIsCleared(t *testing.T) {
	routes := map[string]func(c *candidate){
		"gate failure": func(c *candidate) {
			c.paused = true
			c.tick()
		},
		"explicit release": func(c *candidate) {
			c.elector.Release(context.Background())
		},
	}

	for name, release := range routes {
		t.Run(name, func(t *testing.T) {
			rig := newElectorRig(t)
			a := rig.newCandidate("aaaa")
			a.tick()
			a.requireLeader(1)

			rig.advance(electionInterval)

			var leaderWhenCleared, seen bool

			rig.api.inject(fault{
				method: http.MethodPut,
				path:   leasePath,
				times:  1,
				onFire: func(*fakeAPI) {
					_, _, _, leaderWhenCleared = a.elector.Leadership()
					seen = true
				},
			})

			release(a)

			if seen == false {
				t.Fatal("the clearing write never reached the API")
			}

			if leaderWhenCleared {
				t.Error("this agent still reported leadership when the clearing write reached the API")
			}

			if got := rig.holder(); got != "" {
				t.Errorf("holder = %q, want the Lease cleared", got)
			}

			if a.lost != 1 {
				t.Errorf("onLost ran %d times, want 1", a.lost)
			}
		})
	}
}

// Intent: the snapshot loop stamps a snapshot with the transition count and the Lease epoch it fences on, so the
// two must come from one moment: the epoch of the tenure the count belongs to.
func TestElector_Leadership_ReportsTheTenuresTransitionsAndEpochTogether(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	b := rig.newCandidate("bbbb")

	if transitions, uid, created, ok := a.elector.Leadership(); ok || (transitions != 0) || (uid != "") || (created != "") {
		t.Fatalf("Leadership before any tick = (%d, %q, %q, %v), want zero values", transitions, uid, created, ok)
	}

	a.tick()
	b.tick()

	transitions, uid, created, ok := a.elector.Leadership()
	if (ok == false) || (transitions != 1) || (uid != stringAt(rig.lease(), "metadata", "uid")) || (created != "2026-10-11T12:00:00Z") {
		t.Errorf("holder's Leadership = (%d, %q, %q, %v), want (1, the Lease's uid and creation time, true)", transitions, uid, created, ok)
	}

	if transitions, uid, created, ok := b.elector.Leadership(); ok || (transitions != 0) || (uid != "") || (created != "") {
		t.Errorf("standby's Leadership = (%d, %q, %q, %v), want zero values", transitions, uid, created, ok)
	}

	// IsLeader answers from the same snapshot.
	if got, isLeader := a.elector.IsLeader(); (isLeader == false) || (got != transitions) {
		t.Errorf("IsLeader = (%d, %v), want (%d, true)", got, isLeader, transitions)
	}
}

// Intent: seeing a different Lease (one recreated by a re-enable) must not change what a holder reports about its
// own tenure. Its count and its epoch change together, when it wins in the new epoch, and never one without the
// other.
func TestElector_Leadership_ObservingAnotherEpochDoesNotMixItIntoTheTenure(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	firstUID := stringAt(rig.lease(), "metadata", "uid")

	recreated := map[string]any{
		"metadata": map[string]any{"uid": "uid-recreated", "creationTimestamp": "2030-01-01T00:00:00Z", "resourceVersion": "9"},
		"spec":     map[string]any{"leaseTransitions": float64(40)},
	}

	a.elector.observe(recreated)

	if uid, created := a.elector.LeaseEpoch(); (uid != "uid-recreated") || (created != "2030-01-01T00:00:00Z") {
		t.Errorf("LeaseEpoch = (%q, %q), want the Lease just observed", uid, created)
	}

	transitions, uid, created, ok := a.elector.Leadership()
	if (ok == false) || (transitions != 1) || (uid != firstUID) || (created != "2026-10-11T12:00:00Z") {
		t.Fatalf("Leadership after observing another epoch = (%d, %q, %q, %v), want the old tenure unchanged (1, %q, ...)", transitions, uid, created, ok, firstUID)
	}

	a.elector.won(recreated, rig.clock.Now(), 41)

	transitions, uid, created, ok = a.elector.Leadership()
	if (ok == false) || (transitions != 41) || (uid != "uid-recreated") || (created != "2030-01-01T00:00:00Z") {
		t.Errorf("Leadership after winning in the new epoch = (%d, %q, %q, %v), want (41, uid-recreated, 2030-01-01T00:00:00Z, true)", transitions, uid, created, ok)
	}
}

// Intent: across real ticks that replace the Lease, every snapshot a reader takes pairs a transition count with
// the epoch it was won in, and a holder that lost its Lease reports nothing. Each epoch starts its count at a
// different value, so a count carried into the wrong epoch (or the other way round) cannot look right.
func TestElector_Leadership_IsConsistentAcrossEpochChanges(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")

	type reading struct {
		transitions int64
		uid         string
		ok          bool
	}

	var readings []reading

	record := func() {
		transitions, uid, _, ok := a.elector.Leadership()
		readings = append(readings, reading{transitions, uid, ok})
	}

	// Every request the elector makes is a point where its state can be read mid-tick.
	rig.api.inject(fault{method: http.MethodGet, path: leasePath, times: -1, onFire: func(*fakeAPI) { record() }})
	rig.api.inject(fault{method: http.MethodPut, path: leasePath, times: -1, onFire: func(*fakeAPI) { record() }})

	for epoch := 1; epoch <= 4; epoch++ {
		a.tick()
		rig.advance(electionInterval)
		a.tick()
		record()

		// A re-enable: the Lease is deleted and created again, with the count started at a value of its own.
		rig.api.mu.Lock()
		rig.api.deleteLocked(leasePath)
		rig.api.mu.Unlock()

		lease := leaseManifest(true)
		lease["spec"] = map[string]any{"leaseTransitions": float64(epoch * 10)}
		rig.api.seed(leasePath, lease)
	}

	// The epochs are uid-1 (count 0, so a tenure counts 1), uid-2 (10, tenure 11), uid-3 (20, 21) and so on.
	for i, r := range readings {
		if r.ok == false {
			if (r.transitions != 0) || (r.uid != "") {
				t.Errorf("reading %d: not leading but reported (%d, %q)", i, r.transitions, r.uid)
			}

			continue
		}

		var epoch int64
		if _, err := fmt.Sscanf(r.uid, "uid-%d", &epoch); err != nil {
			t.Errorf("reading %d: leading with the epoch %q", i, r.uid)

			continue
		}

		if want := (epoch-1)*10 + 1; r.transitions != want {
			t.Errorf("reading %d: leading at count %d in epoch %q, want %d", i, r.transitions, r.uid, want)
		}
	}

	if len(readings) < 8 {
		t.Fatalf("only %d readings were taken", len(readings))
	}
}

// Intent: onLost drops the snapshots of a tenure, so it runs once when a tenure ends. A holder that lost the
// Lease to the renew deadline has already announced it, so neither handing back the claim that is still on the
// record nor a gate failure afterwards ends anything new.
func TestElector_OnLostFiresOncePerTenure_WhenAReleaseFollowsADeadlineLoss(t *testing.T) {
	cases := []struct {
		name       string
		act        func(c *candidate)
		wantHolder string
	}{
		// An explicit release clears whatever claim of this agent is on the record.
		{"explicit release", func(c *candidate) { c.elector.Release(context.Background()) }, ""},
		// A gate failure releases only a holder, and this agent no longer is one; the record is left to expire.
		{"gate failure", func(c *candidate) {
			c.paused = true
			c.tick()
		}, "vord-aaaa"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			rig := newElectorRig(t)
			a := rig.newCandidate("aaaa")
			a.tick()

			rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: 1})

			rig.advance(electionInterval)
			a.tick()

			rig.advance(21 * time.Second)
			a.requireNotLeader()

			if a.lost != 1 {
				t.Fatalf("onLost ran %d times for the deadline loss, want 1", a.lost)
			}

			// The record still names this agent, so there is a claim for a release to clear.
			if got := rig.holder(); got != "vord-aaaa" {
				t.Fatalf("holder = %q, want the record to still name this agent", got)
			}

			tc.act(a)

			if got := rig.holder(); got != tc.wantHolder {
				t.Errorf("holder = %q, want %q", got, tc.wantHolder)
			}

			if a.lost != 1 {
				t.Errorf("onLost ran %d times in total, want once for the one tenure", a.lost)
			}
		})
	}
}

// Intent: a new tenure announces its own end: a holder that lost the Lease to the deadline and then renewed it in
// place leads a second tenure, and losing that one is announced too.
func TestElector_OnLostFiresForEachTenure(t *testing.T) {
	rig := newElectorRig(t)
	a := rig.newCandidate("aaaa")
	a.tick()

	rig.api.inject(fault{method: http.MethodPut, path: leasePath, status: http.StatusInternalServerError, reason: "InternalError", times: 1})

	rig.advance(electionInterval)
	a.tick()

	rig.advance(21 * time.Second)
	a.requireNotLeader()
	a.tick()
	a.requireLeader(1)

	if a.lost != 1 {
		t.Fatalf("onLost ran %d times after the first tenure, want 1", a.lost)
	}

	a.elector.Release(context.Background())

	if a.lost != 2 {
		t.Errorf("onLost ran %d times after the second tenure, want 2", a.lost)
	}
}
