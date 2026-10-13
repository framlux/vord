// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"context"
	"fmt"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/protobuf/encoding/protojson"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/kubereporter"
	pb "github.com/framlux/vord/internal/proto/agent"
)

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
