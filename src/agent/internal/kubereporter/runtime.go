// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"log/slog"
	"sync"
	"time"

	"github.com/framlux/vord/internal/kube"
)

// stopTimeout bounds the release of the Lease when the runtime stops by itself or the agent shuts down. The
// release runs on a context of its own, because the context that ended the runtime is already cancelled.
const stopTimeout = 5 * time.Second

// RuntimeOptions configures a Runtime.
type RuntimeOptions struct {
	// DataDir is the agent data directory, which holds k8s-reporter.json.
	DataDir string
	// Clock is the time source of the election. Nil means the system clock.
	Clock Clock
	// Gate is the health gate the elector checks before it contends and while it holds the Lease.
	Gate Gate
	// OnLost drops the cluster snapshots this agent queued. It runs when this agent stops being the holder of
	// the Lease, however that came about, and once when the runtime starts on an enrolled host, since whatever a
	// previous run left in the queue (a crash while leading) belongs to a tenure nothing vouches for any more.
	// It must be safe to call from any goroutine.
	OnLost func()
	// Ticks drives the loop; each value runs one step. Nil means a ticker every 20 seconds, which is what an
	// agent uses. Tests supply a channel so they never wait for time to pass.
	Ticks <-chan time.Time
}

// Runtime keeps the cluster reporter running on an enrolled host. Every 20 seconds it looks for the state file:
// when one appears it builds the client and the elector from it, when it changes it starts over with the new
// credentials, and when it is gone it releases the Lease and stops. Enrol and unenrol therefore take effect
// without an agent restart.
//
// The candidate's identity on the Lease comes from the instance id in the state file, so a host cloned after it
// enrolled shares the identity with its original; see Elector.
type Runtime struct {
	dataDir string
	clock   Clock
	gate    Gate
	onLost  func()
	ticks   <-chan time.Time

	// stepMu serialises a loop step with a release, so the loop never contends for the Lease again between a
	// release and the deletion of the state file.
	stepMu sync.Mutex

	// mu guards the fields below, which other goroutines read.
	mu           sync.Mutex
	current      *runningElector
	cancelStep   context.CancelFunc
	heldOffState State
	heldOffUntil time.Time
}

// runningElector is the elector built from one version of the state file.
type runningElector struct {
	state   State
	elector *Elector
}

// A Runtime is what the reporter commands hand the Lease back through.
var _ LeaseReleaser = (*Runtime)(nil)

// NewRuntime builds a Runtime. It does nothing until Run is called.
func NewRuntime(opts RuntimeOptions) *Runtime {
	r := &Runtime{
		dataDir: opts.DataDir,
		clock:   opts.Clock,
		gate:    opts.Gate,
		onLost:  opts.OnLost,
		ticks:   opts.Ticks,
	}

	if r.clock == nil {
		r.clock = systemClock{}
	}

	return r
}

// Run drives the runtime until ctx ends, then releases the Lease on a fresh context so a graceful shutdown hands
// it back at once instead of leaving a standby to wait out the lease duration.
func (r *Runtime) Run(ctx context.Context) {
	ticks := r.ticks
	if ticks == nil {
		ticker := time.NewTicker(electionInterval)
		defer ticker.Stop()

		ticks = ticker.C
	}

	defer r.shutdown()

	if ctx.Err() != nil {
		return
	}

	r.dropSnapshotsOfAPreviousRun()

	for {
		r.step(ctx)

		select {
		case <-ctx.Done():
			return
		case <-ticks:
		}
	}
}

// dropSnapshotsOfAPreviousRun drops the cluster snapshots left in the queue by an earlier run, when this host is
// enrolled. It runs before the first election request, so no snapshot of an old tenure can sit beside one of the
// new; a holder collects immediately on winning, so nothing is lost by it.
func (r *Runtime) dropSnapshotsOfAPreviousRun() {
	if r.onLost == nil {
		return
	}

	if _, found, err := LoadState(r.dataDir); (err != nil) || (found == false) {
		return
	}

	r.onLost()
}

// ReleaseAndStop hands the Lease back if this host holds it and stops the elector, so unenrol and uninstall can
// delete what the token reaches afterwards. The loop is kept from starting an elector again for the same
// credentials for a lease duration, which covers the time the command still needs to remove the state file and
// heals itself if the command fails and leaves the host enrolled.
func (r *Runtime) ReleaseAndStop(ctx context.Context) {
	// A step in flight may be waiting on a slow API; ending it lets this release go first.
	r.mu.Lock()
	if r.cancelStep != nil {
		r.cancelStep()
	}
	r.mu.Unlock()

	r.stepMu.Lock()
	defer r.stepMu.Unlock()

	r.mu.Lock()
	cur := r.current
	r.current = nil
	r.mu.Unlock()

	// The credentials to hold off are those of the elector just stopped, or, when none had started yet, the ones on
	// disk that the loop would otherwise start from.
	var state State

	if cur != nil {
		state = cur.state
	} else if loaded, found, err := LoadState(r.dataDir); (err == nil) && found {
		state = loaded
	}

	r.mu.Lock()
	r.heldOffState = state
	r.heldOffUntil = r.clock.Now().Add(releaseBackoff)
	r.mu.Unlock()

	if cur != nil {
		cur.elector.Release(ctx)
	}
}

// ReporterError returns why the reporter on this host cannot use its Lease credentials: credentials_rejected,
// tls_ca_changed, or an empty string when there is no error or no elector.
func (r *Runtime) ReporterError() string {
	r.mu.Lock()
	cur := r.current
	r.mu.Unlock()

	if cur == nil {
		return ""
	}

	return cur.elector.ReporterError()
}

// step runs one pass of the loop: it brings the elector in line with the state file, then runs one election step.
func (r *Runtime) step(ctx context.Context) {
	if ctx.Err() != nil {
		return
	}

	r.stepMu.Lock()
	defer r.stepMu.Unlock()

	stepCtx, cancel := context.WithCancel(ctx)

	r.mu.Lock()
	r.cancelStep = cancel
	r.mu.Unlock()

	defer func() {
		cancel()

		r.mu.Lock()
		r.cancelStep = nil
		r.mu.Unlock()
	}()

	r.reconcile(stepCtx)

	r.mu.Lock()
	cur := r.current
	r.mu.Unlock()

	if cur != nil {
		cur.elector.Tick(stepCtx)
	}
}

// reconcile starts, restarts or stops the elector to match the state file. A state file that cannot be read
// leaves everything as it is: a running reporter is not dropped over a file that may be mid-rewrite.
func (r *Runtime) reconcile(ctx context.Context) {
	state, found, err := LoadState(r.dataDir)
	if err != nil {
		slog.Warn("the cluster reporter state file cannot be used, leaving the reporter as it is", "error", err)

		return
	}

	r.mu.Lock()
	cur := r.current
	r.mu.Unlock()

	if found == false {
		if cur != nil {
			slog.Info("the cluster reporter state file is gone, stopping the reporter")
			r.stop(ctx, cur)
		}

		return
	}

	if cur != nil {
		if cur.state == state {
			return
		}

		slog.Info("the cluster reporter credentials changed, restarting the reporter")
		r.stop(ctx, cur)
	}

	if r.isHeldOff(state) {
		return
	}

	slog.Info("starting the cluster reporter", "instance", state.InstanceID)

	elector := NewElector(clientFactoryFor(state), "vord-"+state.InstanceID, r.clock, r.gate, r.onLost)

	r.mu.Lock()
	r.current = &runningElector{state: state, elector: elector}
	r.mu.Unlock()
}

// stop releases a running elector and forgets it. The release gets a context of its own: a step that was
// interrupted by ReleaseAndStop has a cancelled one, and the Lease must still be handed back.
func (r *Runtime) stop(ctx context.Context, cur *runningElector) {
	r.mu.Lock()
	if r.current == cur {
		r.current = nil
	}
	r.mu.Unlock()

	releaseCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), stopTimeout)
	defer cancel()

	cur.elector.Release(releaseCtx)
}

// isHeldOff reports whether ReleaseAndStop recently stopped the elector for exactly these credentials.
func (r *Runtime) isHeldOff(state State) bool {
	r.mu.Lock()
	defer r.mu.Unlock()

	return (r.heldOffState == state) && r.clock.Now().Before(r.heldOffUntil)
}

// shutdown releases the Lease when the agent stops.
func (r *Runtime) shutdown() {
	r.stepMu.Lock()
	defer r.stepMu.Unlock()

	r.mu.Lock()
	cur := r.current
	r.current = nil
	r.mu.Unlock()

	if cur == nil {
		return
	}

	releaseCtx, cancel := context.WithTimeout(context.Background(), stopTimeout)
	defer cancel()

	cur.elector.Release(releaseCtx)
}

// clientFactoryFor builds the client for the reporter's credentials, verified against the cluster CA recorded at
// enrol and nothing else.
func clientFactoryFor(state State) func() (*kube.Client, error) {
	return func() (*kube.Client, error) {
		return kube.New(kube.Config{
			Server:        state.Server,
			CAData:        []byte(state.CACert),
			TLSServerName: state.TLSServerName,
			Token:         state.Token,
		})
	}
}
