// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/framlux/vord/internal/kube"
)

// The election follows client-go's leader election: a holder renews a Lease on every tick, a candidate takes a
// Lease only when its holder is gone or has stood still for a whole lease duration on the candidate's own clock,
// and every write is a compare-and-swap on the resourceVersion that was read.
const (
	// electionInterval is how often Tick is called.
	electionInterval = 20 * time.Second

	// leaseDuration is how long a record may stand still before another candidate takes the Lease from its holder.
	leaseDuration = 60 * time.Second

	// renewDeadline is how long a holder may go without a successful renew before it stops acting as leader.
	renewDeadline = 40 * time.Second

	// leaseRequestTimeout bounds each request to the Lease. It is shorter than electionInterval, so a hung request
	// cannot make a tick run into the next one.
	leaseRequestTimeout = 10 * time.Second

	// releaseBackoff is how long an agent that gave the Lease up stays out of the election.
	releaseBackoff = leaseDuration

	// releaseAttempts is how often a release is retried after a Conflict.
	releaseAttempts = 3
)

// The limits of the health gate.
const (
	// gateMaxAckAge is how long the holder may go without the server acknowledging a batch.
	gateMaxAckAge = 5 * time.Minute

	// gateMaxSnapshotAge is how old a cluster snapshot may be while still waiting to be delivered.
	gateMaxSnapshotAge = 15 * time.Minute

	// gateMaxErrorSnapshots is how many error snapshots in a row the holder may produce.
	gateMaxErrorSnapshots = 3
)

// Clock is the time source the election runs on. Its readings carry a monotonic component, so a step in the wall
// clock cannot stretch or shrink a deadline; tests supply a fake.
type Clock interface {
	Now() time.Time
}

// systemClock is the Clock of a running agent.
type systemClock struct{}

// Now returns the current time with its monotonic reading.
func (systemClock) Now() time.Time {
	return time.Now()
}

// Gate is what a candidate must pass to hold, or to contend for, the Lease. It keeps a host that cannot deliver
// telemetry from holding the Lease and starving healthy standbys. A nil function stands for a rule that is always
// met.
type Gate struct {
	// Paused reports whether the server has paused this host.
	Paused func() bool
	// LastAck returns when the server last acknowledged a batch; the zero time means it has not yet.
	LastAck func() time.Time
	// StartedAt is when the agent started. The last ack counts from this moment until a first ack arrives, so
	// a freshly started holder does not fail its own gate before its first send.
	StartedAt time.Time
	// OldestPendingSnapshot returns the creation time of the oldest cluster snapshot that is pending or
	// processing in the queue, and false when there is none.
	OldestPendingSnapshot func() (time.Time, bool)
	// ConsecutiveErrorSnaps returns how many error snapshots the holder has produced in a row.
	ConsecutiveErrorSnaps func() int
}

// Elector runs one candidate's side of the election for the reporter Lease. Tick and Release make requests and
// are serialised; IsLeader, LeaseEpoch and ReporterError only read, and may be called from any goroutine.
type Elector struct {
	clientFactory func() (*kube.Client, error)
	identity      string
	clock         Clock
	gate          Gate
	onLost        func()

	// withTimeout bounds each Lease request. It is context.WithTimeout, and a test replaces it to see the limits.
	withTimeout func(ctx context.Context, d time.Duration) (context.Context, context.CancelFunc)

	// opMu serialises the operations that make requests, and guards the fields below it.
	opMu         sync.Mutex
	client       *kube.Client
	backoffUntil time.Time
	observed     bool
	observedSpec string
	observedAt   time.Time
	lastFailure  string

	// mu guards what the readers see.
	mu           sync.Mutex
	held         bool
	transitions  int64
	renewedFrom  time.Time
	leaseUID     string
	leaseCreated string
	reporterErr  string
}

// NewElector builds a candidate. clientFactory builds the client the Lease is read and written with, and is
// called again after a failure until it succeeds. identity is the name the candidate holds the Lease under.
// onLost runs when the candidate stops being the holder, however that came about, and must be safe to call from
// any goroutine.
func NewElector(clientFactory func() (*kube.Client, error), identity string, clock Clock, gate Gate, onLost func()) *Elector {
	if clock == nil {
		clock = systemClock{}
	}

	return &Elector{
		clientFactory: clientFactory,
		identity:      identity,
		clock:         clock,
		gate:          gate,
		onLost:        onLost,
		withTimeout:   context.WithTimeout,
	}
}

// IsLeader reports whether this agent holds the Lease and renewed it within the renew deadline. When the
// deadline has passed it answers false at once, and announces the loss, instead of waiting for the next tick.
func (e *Elector) IsLeader() (transitions int64, ok bool) {
	e.loseIfExpired(e.clock.Now())

	e.mu.Lock()
	defer e.mu.Unlock()

	return e.transitions, e.held
}

// LeaseEpoch returns the metadata.uid and creationTimestamp of the Lease as last observed. They tell one Lease
// from the next one created after the cluster was disabled and enabled again.
func (e *Elector) LeaseEpoch() (uid string, createdAt string) {
	e.mu.Lock()
	defer e.mu.Unlock()

	return e.leaseUID, e.leaseCreated
}

// ReporterError returns credentials_rejected when the cluster refused this agent's token, tls_ca_changed when it
// could not verify the cluster's certificate, and an empty string otherwise. It describes the last Lease call
// that said anything about the credentials: a successful call clears it, and a failure of another kind leaves it
// as it was.
func (e *Elector) ReporterError() string {
	e.mu.Lock()
	defer e.mu.Unlock()

	return e.reporterErr
}

// Tick runs one election step: it drops leadership if the renew deadline has passed, checks the health gate,
// and then renews, acquires or watches the Lease. It is called every electionInterval.
func (e *Elector) Tick(ctx context.Context) {
	e.opMu.Lock()
	defer e.opMu.Unlock()

	now := e.clock.Now()
	e.loseIfExpired(now)

	if now.Before(e.backoffUntil) {
		return
	}

	if reason := e.gateFailure(now); reason != "" {
		if e.isHeld() {
			slog.Warn("giving up the cluster reporter lease because this host is not delivering", "identity", e.identity, "reason", reason)
			e.releaseLocked(ctx)
		} else {
			slog.Debug("not contending for the cluster reporter lease", "identity", e.identity, "reason", reason)
		}

		return
	}

	e.step(ctx)
}

// Release clears the holder of the Lease if this agent is the holder, so a standby can take over at once, and
// stops this agent acting as leader. The agent then stays out of the election for a lease duration.
func (e *Elector) Release(ctx context.Context) {
	e.opMu.Lock()
	defer e.opMu.Unlock()

	e.releaseLocked(ctx)
}

// gateFailure names the first rule of the health gate that fails, or returns an empty string when all hold.
func (e *Elector) gateFailure(now time.Time) string {
	if (e.gate.Paused != nil) && e.gate.Paused() {
		return "host_paused"
	}

	counted := e.gate.StartedAt
	if e.gate.LastAck != nil {
		if acked := e.gate.LastAck(); acked.After(counted) {
			counted = acked
		}
	}

	if now.Sub(counted) >= gateMaxAckAge {
		return "no_recent_ack"
	}

	if e.gate.OldestPendingSnapshot != nil {
		if created, pending := e.gate.OldestPendingSnapshot(); pending && (now.Sub(created) >= gateMaxSnapshotAge) {
			return "snapshot_not_delivered"
		}
	}

	if (e.gate.ConsecutiveErrorSnaps != nil) && (e.gate.ConsecutiveErrorSnaps() >= gateMaxErrorSnapshots) {
		return "consecutive_error_snapshots"
	}

	return ""
}

// step reads the Lease and renews it, takes it or leaves it alone.
func (e *Elector) step(ctx context.Context) {
	client, err := e.leaseClient()
	if err != nil {
		e.logFailure("build the client", err)

		return
	}

	lease, err := e.readLease(ctx, client)
	if err != nil {
		if kube.IsNotFound(err) && e.loseHolding("the lease no longer exists") {
			return
		}

		e.logFailure("read the lease", err)

		return
	}

	e.observe(lease)

	holder := stringAt(lease, "spec", "holderIdentity")
	wasHeld := e.isHeld()

	if holder == e.identity {
		e.write(ctx, client, lease, false, wasHeld)

		return
	}

	if wasHeld {
		e.loseHolding("another agent holds the lease")
	}

	if (holder != "") && (e.clock.Now().Sub(e.observedAt) < leaseDuration) {
		return
	}

	e.write(ctx, client, lease, true, false)
}

// write puts the Lease back with this agent as its holder. A new acquisition (acquiring) starts a new tenure:
// its acquireTime is now and its transition count goes up by one. Anything else is a renew of a claim that the
// record already holds, which keeps both. renewing says the agent has been leading and still believes it does,
// which is what earns the one retry after a Conflict.
//
// The Lease is edited as the raw JSON that was read, replacing only the fields the election owns, so a field this
// agent does not know survives.
func (e *Elector) write(ctx context.Context, client *kube.Client, lease map[string]any, acquiring bool, renewing bool) {
	next := copyObject(lease)
	spec := copyObject(leaseSpec(lease))
	now := formatMicroTime(e.clock.Now())

	transitions := intAt(spec, "leaseTransitions")

	if acquiring {
		transitions++
		spec["acquireTime"] = now
	} else if _, hasAcquireTime := spec["acquireTime"]; hasAcquireTime == false {
		spec["acquireTime"] = now
	}

	spec["holderIdentity"] = e.identity
	spec["leaseDurationSeconds"] = int64(leaseDuration / time.Second)
	spec["renewTime"] = now
	spec["leaseTransitions"] = transitions
	next["spec"] = spec

	// The renew deadline counts from before the request was sent: that is the last moment the Lease is known to
	// have been ours, however long the answer took to come back.
	sentAt := e.clock.Now()

	stored, err := e.putLease(ctx, client, next)
	if err == nil {
		e.won(stored, sentAt, transitions)

		return
	}

	if kube.IsConflict(err) == false {
		e.logFailure("write the lease", err)

		return
	}

	e.afterConflict(ctx, client, renewing)
}

// afterConflict handles a write that lost its compare-and-swap: something changed the Lease since it was read.
// The Lease is read again to learn who holds it. An agent that was leading and still finds itself the holder
// (typically after an enrol changed an annotation) renews once more with the new resourceVersion; an agent that
// finds another holder is no longer leading; an agent that was only trying to take the Lease does not hold it.
func (e *Elector) afterConflict(ctx context.Context, client *kube.Client, renewing bool) {
	lease, err := e.readLease(ctx, client)
	if err != nil {
		e.logFailure("read the lease again", err)

		return
	}

	e.observe(lease)

	if stringAt(lease, "spec", "holderIdentity") != e.identity {
		if renewing {
			e.loseHolding("another agent took the lease")
		}

		return
	}

	if renewing == false {
		return
	}

	next := copyObject(lease)
	spec := copyObject(leaseSpec(lease))
	spec["leaseDurationSeconds"] = int64(leaseDuration / time.Second)
	spec["renewTime"] = formatMicroTime(e.clock.Now())
	next["spec"] = spec

	sentAt := e.clock.Now()

	stored, err := e.putLease(ctx, client, next)
	if err != nil {
		e.logFailure("renew the lease again", err)

		return
	}

	e.won(stored, sentAt, intAt(spec, "leaseTransitions"))
}

// won records a successful write: this agent holds the Lease, as of sentAt.
func (e *Elector) won(stored map[string]any, sentAt time.Time, transitions int64) {
	e.observe(stored)

	e.mu.Lock()
	wasHeld := e.held
	e.held = true
	e.transitions = transitions
	e.renewedFrom = sentAt
	e.mu.Unlock()

	if wasHeld == false {
		slog.Info("this host now holds the cluster reporter lease", "identity", e.identity, "transitions", transitions)
	}
}

// releaseLocked clears the Lease if this agent is its holder, then stops acting as leader and backs off. The
// local state changes even if the Lease could not be written: an agent told to give the Lease up must stop
// leading whether or not the cluster heard it.
func (e *Elector) releaseLocked(ctx context.Context) {
	cleared := false

	if client, err := e.leaseClient(); err == nil {
		cleared = e.clearHolder(ctx, client)
	} else {
		e.logFailure("build the client", err)
	}

	wasHeld := e.setNotHeld()
	e.backoffUntil = e.clock.Now().Add(releaseBackoff)

	if wasHeld || cleared {
		slog.Info("this host gave up the cluster reporter lease", "identity", e.identity)
		e.fireLost()
	}
}

// clearHolder empties holderIdentity if the Lease names this agent, retrying when another writer got in first.
// It reports whether it cleared the holder.
func (e *Elector) clearHolder(ctx context.Context, client *kube.Client) bool {
	for attempt := 0; attempt < releaseAttempts; attempt++ {
		lease, err := e.readLease(ctx, client)
		if err != nil {
			if kube.IsNotFound(err) == false {
				e.logFailure("read the lease to release it", err)
			}

			return false
		}

		e.observe(lease)

		if stringAt(lease, "spec", "holderIdentity") != e.identity {
			return false
		}

		next := copyObject(lease)
		spec := copyObject(leaseSpec(lease))
		spec["holderIdentity"] = ""
		spec["renewTime"] = formatMicroTime(e.clock.Now())
		next["spec"] = spec

		stored, err := e.putLease(ctx, client, next)
		if err == nil {
			e.observe(stored)

			return true
		}

		if kube.IsConflict(err) == false {
			e.logFailure("release the lease", err)

			return false
		}
	}

	return false
}

// readLease reads the Lease under its own timeout.
func (e *Elector) readLease(ctx context.Context, client *kube.Client) (map[string]any, error) {
	callCtx, cancel := e.withTimeout(ctx, leaseRequestTimeout)
	defer cancel()

	lease, err := client.GetRaw(callCtx, leasePath)
	e.recordCall(err)

	return lease, err
}

// putLease replaces the Lease under its own timeout.
func (e *Elector) putLease(ctx context.Context, client *kube.Client, lease map[string]any) (map[string]any, error) {
	callCtx, cancel := e.withTimeout(ctx, leaseRequestTimeout)
	defer cancel()

	stored, err := client.PutRaw(callCtx, leasePath, lease)
	e.recordCall(err)

	return stored, err
}

// recordCall updates the report of why the credentials do not work from the outcome of a Lease call.
func (e *Elector) recordCall(err error) {
	e.mu.Lock()
	defer e.mu.Unlock()

	switch {
	case err == nil:
		e.reporterErr = ""
	case kube.IsAuthRejected(err):
		e.reporterErr = reasonCredentialsRejected
	case kube.IsTLSVerify(err):
		e.reporterErr = reasonTLSCAChanged
	}
}

// observe notes the Lease as it was just seen. A change to the spec restarts the clock the lease duration is
// measured on; the Lease's identity is kept for the snapshots.
func (e *Elector) observe(lease map[string]any) {
	encoded, err := json.Marshal(leaseSpec(lease))
	if err != nil {
		encoded = nil
	}

	if (e.observed == false) || (string(encoded) != e.observedSpec) {
		e.observed = true
		e.observedSpec = string(encoded)
		e.observedAt = e.clock.Now()
	}

	e.mu.Lock()
	e.leaseUID = stringAt(lease, "metadata", "uid")
	e.leaseCreated = stringAt(lease, "metadata", "creationTimestamp")
	e.mu.Unlock()
}

// leaseClient returns the client for the Lease, building it on first use and again after a failure.
func (e *Elector) leaseClient() (*kube.Client, error) {
	if e.client != nil {
		return e.client, nil
	}

	client, err := e.clientFactory()
	if err != nil {
		return nil, err
	}

	if client == nil {
		return nil, errors.New("the client factory returned no client")
	}

	e.client = client

	return client, nil
}

// isHeld reports whether this agent believes it holds the Lease, without testing the renew deadline.
func (e *Elector) isHeld() bool {
	e.mu.Lock()
	defer e.mu.Unlock()

	return e.held
}

// setNotHeld stops this agent acting as leader and reports whether it was.
func (e *Elector) setNotHeld() bool {
	e.mu.Lock()
	defer e.mu.Unlock()

	wasHeld := e.held
	e.held = false

	return wasHeld
}

// loseHolding ends this agent's leadership for the given reason and announces it. It reports whether the agent
// was leading.
func (e *Elector) loseHolding(reason string) bool {
	if e.setNotHeld() == false {
		return false
	}

	slog.Warn("this host lost the cluster reporter lease", "identity", e.identity, "reason", reason)
	e.fireLost()

	return true
}

// loseIfExpired ends leadership when the last successful renew was sent more than the renew deadline ago. The
// leadership is dropped under the lock, so however many callers notice, only one announces it.
func (e *Elector) loseIfExpired(now time.Time) {
	e.mu.Lock()
	expired := e.held && (now.Sub(e.renewedFrom) > renewDeadline)

	if expired {
		e.held = false
	}

	e.mu.Unlock()

	if expired {
		slog.Warn("this host lost the cluster reporter lease", "identity", e.identity, "reason", "the renew deadline passed")
		e.fireLost()
	}
}

// fireLost tells the owner that this agent no longer leads, so it can drop what it queued as the leader.
func (e *Elector) fireLost() {
	if e.onLost != nil {
		e.onLost()
	}
}

// logFailure records a failed call. A failure that repeats unchanged is logged at debug, so an unreachable API
// does not fill the log every 20 seconds; any change, or the first, is a warning.
func (e *Elector) logFailure(what string, err error) {
	text := fmt.Sprintf("%s: %v", what, err)
	if text == e.lastFailure {
		slog.Debug("cluster reporter lease call failed again", "identity", e.identity, "error", err)

		return
	}

	e.lastFailure = text

	slog.Warn("cluster reporter lease call failed", "identity", e.identity, "action", what, "error", err)
}

// formatMicroTime formats t as the API's MicroTime: RFC 3339 in UTC with exactly six fractional digits.
func formatMicroTime(t time.Time) string {
	return t.UTC().Format("2006-01-02T15:04:05.000000Z07:00")
}

// leaseSpec returns the spec of a Lease object, or an empty map when it has none.
func leaseSpec(lease map[string]any) map[string]any {
	spec, ok := lease["spec"].(map[string]any)
	if ok == false {
		return map[string]any{}
	}

	return spec
}

// copyObject returns a shallow copy of a JSON object, enough to replace its top-level fields without changing
// the original.
func copyObject(obj map[string]any) map[string]any {
	out := make(map[string]any, len(obj))
	for key, value := range obj {
		out[key] = value
	}

	return out
}

// intAt reads an integer field of a JSON object, 0 when it is absent or not a whole number. Objects read with
// GetRaw hold numbers as json.Number, which keeps large integers exact.
func intAt(obj map[string]any, key string) int64 {
	switch value := obj[key].(type) {
	case json.Number:
		number, err := value.Int64()
		if err != nil {
			return 0
		}

		return number
	case float64:
		return int64(value)
	case int64:
		return value
	case int:
		return int64(value)
	}

	return 0
}
