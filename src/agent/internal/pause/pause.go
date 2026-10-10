// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package pause tracks whether the server has paused this host, so the collectors, the sender and the command
// poll can back off together and resume together.
package pause

import (
	"log/slog"
	"sync"
	"time"

	pb "github.com/framlux/vord/internal/proto/agent"
)

// leaseIntervals is how many live ping intervals a pause holds without a renewing ping before the agent
// resumes on its own. A resume the agent never hears (a failed ping, a server that lost the answer) therefore
// strands the host for a bounded time, never indefinitely.
const leaseIntervals = 3

// Reason says why the server is not accepting this host's telemetry.
type Reason int

const (
	// None means the host is not paused.
	None Reason = iota
	// HostPaused means the tenant has more machines than its plan allows and this host is one of the paused ones.
	HostPaused
	// SubscriptionInactive means the tenant's subscription is not active.
	SubscriptionInactive
)

// FromProto maps the wire reason onto the agent's own. Anything that is not a known pause reason, including a
// value from a newer server, means the host is not paused.
func FromProto(r pb.IngestRejectionReason) Reason {
	switch r {
	case pb.IngestRejectionReason_INGEST_REJECTION_REASON_HOST_PAUSED:
		return HostPaused
	case pb.IngestRejectionReason_INGEST_REJECTION_REASON_SUBSCRIPTION_INACTIVE:
		return SubscriptionInactive
	default:
		return None
	}
}

// Hooks are the side effects of a transition. Either may be nil. A hook runs on the goroutine that caused the
// transition and must not call back into the Coordinator.
type Hooks struct {
	// OnPause runs once per transition into the paused state: it clears the queue and closes the streams.
	OnPause func(reason Reason)
	// OnResume runs once per transition out of the paused state, before anything is allowed to collect again:
	// it moves the collectors' cursors to "now" so the pause is not replayed.
	OnResume func()
}

// Coordinator holds the pause state and the lease behind it. Pause state lives in memory only: a restarted agent
// sends normally until its first ack or ping says otherwise.
type Coordinator struct {
	clock        func() time.Time
	pingInterval func() time.Duration
	hooks        Hooks
	logger       *slog.Logger

	// transitionMu serialises every transition, hooks included, so OnPause and OnResume strictly alternate and
	// a collector waiting on an expired lease cannot get past the resume reset. It is always taken before mu,
	// and hooks run holding it but never holding mu.
	transitionMu sync.Mutex

	// mu guards the fields below and is never held across a hook or a log call.
	mu        sync.Mutex
	reason    Reason
	renewedAt time.Time
}

// New creates a Coordinator. clock and pingInterval are injected so tests do not depend on wall-clock time, and
// pingInterval is read on every check because the server can change it while the host is paused.
func New(clock func() time.Time, pingInterval func() time.Duration, hooks Hooks) *Coordinator {
	return &Coordinator{
		clock:        clock,
		pingInterval: pingInterval,
		hooks:        hooks,
		logger:       slog.Default().With("component", "pause"),
	}
}

// Observe records what a ping response or a telemetry ack said about the host. A pause reason starts or renews
// the lease; None ends a pause that is in force. A ping that failed says nothing and must not be passed here.
func (c *Coordinator) Observe(reason Reason) {
	c.transitionMu.Lock()
	defer c.transitionMu.Unlock()

	c.mu.Lock()
	previous := c.reason
	if reason != None {
		c.reason = reason
		c.renewedAt = c.clock()
	}
	c.mu.Unlock()

	switch {
	case reason == None && previous != None:
		c.resume("host resumed by the server: sending telemetry again")
	case reason != None && previous == None:
		c.logPause(reason)
		if c.hooks.OnPause != nil {
			c.hooks.OnPause(reason)
		}
	case reason != None && reason != previous:
		c.logPause(reason)
	}
}

// Paused reports whether the host is paused. A lease that has gone unrenewed for three live ping intervals
// ends the pause here, running OnResume first; every other caller that sees the lapsed lease waits for that
// resume to finish rather than slipping past it.
func (c *Coordinator) Paused() bool {
	paused, expired := c.leaseState()
	if paused == false {
		return false
	}
	if expired == false {
		return true
	}

	c.transitionMu.Lock()
	defer c.transitionMu.Unlock()

	// The lease may have been renewed or the host resumed while this caller waited for its turn.
	paused, expired = c.leaseState()
	if paused == false {
		return false
	}
	if expired == false {
		return true
	}

	c.resume("pause lease expired without a renewing ping: sending telemetry again")

	return false
}

// leaseState reports whether a pause is in force and whether its lease has lapsed.
func (c *Coordinator) leaseState() (paused bool, expired bool) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.reason == None {
		return false, false
	}

	return true, c.clock().Sub(c.renewedAt) > leaseIntervals*c.pingInterval()
}

// resume runs OnResume and only then clears the pause, so Paused() keeps answering true until the collectors'
// cursors have been reset. The caller holds transitionMu.
func (c *Coordinator) resume(message string) {
	if c.hooks.OnResume != nil {
		c.hooks.OnResume()
	}

	c.mu.Lock()
	c.reason = None
	c.mu.Unlock()

	c.logger.Info(message)
}

func (c *Coordinator) logPause(reason Reason) {
	switch reason {
	case HostPaused:
		c.logger.Warn("host paused by the server: the plan's host limit is reached; telemetry is not being sent")
	case SubscriptionInactive:
		c.logger.Warn("subscription inactive: telemetry is not being sent")
	}
}
