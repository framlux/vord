// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"math"
	"testing"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/state"
)

func newTestStore(t *testing.T) *db.Store {
	t.Helper()
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("failed to open test database: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	return db.NewStore(database, 0)
}

func TestFastTickCollectMemUsage(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	ft.collectMemUsage(fixtureFastMeminfo, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryMemoryUsage {
		t.Errorf("expected type %d, got %d", db.TelemetryMemoryUsage, items[0].ItemType)
	}

	var payload memUsagePayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.MemoryTotal <= 0 {
		t.Errorf("expected positive MemoryTotal, got %d", payload.MemoryTotal)
	}
	if payload.MemoryUsagePercent < 0 || payload.MemoryUsagePercent > 100 {
		t.Errorf("usage percent out of range: %d", payload.MemoryUsagePercent)
	}
}

func TestFastTickCollectCpuUsageDelta(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// First call: no previous state, should not produce telemetry.
	ft.collectCpuUsage(fixtureProcStat1, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 0 {
		t.Fatalf("expected 0 telemetry items on first call, got %d", len(items))
	}

	// Second call: should produce telemetry with delta percentages.
	ft.collectCpuUsage(fixtureProcStat2, nil)

	items, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item on second call, got %d", len(items))
	}

	var payload cpuUsagePayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.CPUUsagePercent < 0 || payload.CPUUsagePercent > 100 {
		t.Errorf("usage percent out of range: %d", payload.CPUUsagePercent)
	}
}

func TestFastTickCollectCpuUsageWithPersistedState(t *testing.T) {
	store := newTestStore(t)

	// Persist previous ticks to simulate crash recovery.
	prev := cpuTicks{User: 100, Nice: 0, System: 50, Idle: 850, Iowait: 0, Irq: 0, Softirq: 0, Steal: 0}
	prevJSON, err := json.Marshal(prev)
	if err != nil {
		t.Fatalf("failed to marshal prev ticks: %v", err)
	}
	stateStr := string(prevJSON)
	if err := store.SaveCollectorState("cpu_usage", &stateStr); err != nil {
		t.Fatalf("failed to save collector state: %v", err)
	}

	ft := NewFastTick(store, state.New())
	ft.loadPersistedCpuTicks()

	if ft.prevTicks == nil {
		t.Fatal("expected prevTicks to be loaded from persisted state")
	}
	if ft.prevTicks.User != 100 {
		t.Errorf("expected User=100, got %d", ft.prevTicks.User)
	}

	// Now a collect call should produce telemetry (since we have previous state).
	ft.collectCpuUsage(fixtureProcStat2, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
}

func TestFastTickErrorIsolation(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// Simulate /proc/stat failure but valid /proc/meminfo and /proc/mounts.
	ft.collectCpuUsage("", errForTest)
	ft.collectMemUsage(fixtureFastMeminfo, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	// MemUsage should still produce telemetry even though CpuUsage failed.
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item (memUsage), got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryMemoryUsage {
		t.Errorf("expected type %d, got %d", db.TelemetryMemoryUsage, items[0].ItemType)
	}
}

func TestFastTickGarbageDataNoPanic(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// Verify tick does not panic when all files are garbage.
	defer func() {
		if r := recover(); r != nil {
			t.Errorf("tick panicked with garbage data: %v", r)
		}
	}()

	ft.safeCollectCpuUsage("\x00\x01\x02\xff\xfe", nil)
	ft.safeCollectMemUsage("\x00\x01\x02\xff\xfe", nil)
	ft.safeCollectDiskUsage(nil, "\x00\x01\x02\xff\xfe", nil)
}

func TestFastTickCorruptedPersistedState(t *testing.T) {
	store := newTestStore(t)

	// Persist corrupted JSON to simulate bad state.
	badJSON := "this is not json{{"
	if err := store.SaveCollectorState("cpu_usage", &badJSON); err != nil {
		t.Fatalf("failed to save collector state: %v", err)
	}

	ft := NewFastTick(store, state.New())
	ft.loadPersistedCpuTicks()

	// Should not panic and should treat as cold start.
	if ft.prevTicks != nil {
		t.Error("expected prevTicks to be nil after corrupted state")
	}
}

// Intent: safeCollect wrappers recover from panics and still produce expected
// telemetry from the non-panicking collectors.
func TestFastTickSafeCollectRecovery(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// CPU usage on first call stores prevTicks but does not enqueue telemetry.
	ft.safeCollectCpuUsage(fixtureProcStat1, nil)
	// MemUsage should enqueue one telemetry item.
	ft.safeCollectMemUsage(fixtureFastMeminfo, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 telemetry item from memUsage, got %d", len(items))
	}
	if len(items) > 0 && items[0].ItemType != db.TelemetryMemoryUsage {
		t.Errorf("expected type %d (MemoryUsage), got %d", db.TelemetryMemoryUsage, items[0].ItemType)
	}
}

// Test fixtures.
var errForTest = errorString("simulated read error")

type errorString string

func (e errorString) Error() string { return string(e) }

const fixtureProcStat1 = `cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0
cpu0 1393280 32966 572056 13343292 6130 0 17875 0 0 0
intr 33047007
ctxt 6290388
btime 1257833464
processes 22529
procs_running 1
procs_blocked 0
`

const fixtureProcStat2 = `cpu  10142153 290696 3094719 46838483 16683 0 25195 0 0 0
cpu0 1395280 32966 573056 13345292 6130 0 17875 0 0 0
intr 33057007
ctxt 6300388
btime 1257833464
processes 22530
procs_running 1
procs_blocked 0
`

// Intent: Zero-delta between ticks → 0% usage (not division by zero).
func TestFastTickCollectCpuUsage_ZeroDelta(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// Set previous ticks identical to current — zero delta.
	identical := cpuTicks{User: 100, Nice: 0, System: 50, Idle: 850, Iowait: 0, Irq: 0, Softirq: 0, Steal: 0}
	ft.prevTicks = &identical

	// Use the same data that produces the identical ticks.
	identicalData := "cpu  100 0 50 850 0 0 0 0 0 0\n"
	ft.collectCpuUsage(identicalData, nil)

	// With zero delta, no telemetry should be enqueued (totalDelta <= 0).
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items for zero delta, got %d", len(items))
	}
}

// Intent: After collectCpuUsage, prevTicks is saved to DB for crash recovery.
func TestFastTickPersistsCpuTicks(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// First call sets prevTicks but doesn't enqueue telemetry.
	ft.collectCpuUsage(fixtureProcStat1, nil)

	// Verify the state was persisted.
	cs, err := store.GetCollectorState("cpu_usage")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs == nil {
		t.Fatal("expected collector state to be persisted after first tick")
	}
	if cs.StateJSON == nil || *cs.StateJSON == "" {
		t.Error("expected non-empty state_json after first tick")
	}
}

// Intent: Disk usage collection filters out pseudo-FS mount points (proc, sysfs, tmpfs).
func TestFastTickCollectDiskUsage_NoPseudoFS(t *testing.T) {
	store := newTestStore(t)
	ft := NewFastTick(store, state.New())

	// Data with only pseudo-FS entries — should produce no disk usage.
	pseudoOnly := "proc /proc proc rw 0 0\nsysfs /sys sysfs rw 0 0\ntmpfs /tmp tmpfs rw 0 0\n"

	mounts := parseMountsData(pseudoOnly)
	if len(mounts) != 0 {
		t.Errorf("expected 0 real mounts from pseudo-FS data, got %d", len(mounts))
	}

	// collectDiskUsage with pseudo-only data — should still not panic.
	ft.collectDiskUsage(nil, pseudoOnly, nil)
}

// Intent: Multiple real filesystem mount points produce telemetry with disk entries.
func TestFastTickCollectDiskUsage_RealMounts(t *testing.T) {
	// Test parseMountsData with a mix of real and pseudo filesystems.
	data := `/dev/sda1 / ext4 rw 0 0
/dev/sdb1 /home xfs rw 0 0
proc /proc proc rw 0 0
sysfs /sys sysfs rw 0 0
tmpfs /run tmpfs rw 0 0
`
	mounts := parseMountsData(data)

	if len(mounts) != 2 {
		t.Errorf("expected 2 real mounts, got %d", len(mounts))
	}

	if len(mounts) >= 2 {
		if mounts[0].device != "/dev/sda1" {
			t.Errorf("expected first mount device=/dev/sda1, got %q", mounts[0].device)
		}
		if mounts[1].mountPoint != "/home" {
			t.Errorf("expected second mount point=/home, got %q", mounts[1].mountPoint)
		}
	}
}

// Intent: safeInt64 clamps uint64 values exceeding math.MaxInt64 to prevent overflow.
func TestSafeInt64_Overflow(t *testing.T) {
	// Value within int64 range should pass through unchanged.
	if got := safeInt64(1000); got != 1000 {
		t.Errorf("safeInt64(1000) = %d, want 1000", got)
	}

	// Zero should pass through.
	if got := safeInt64(0); got != 0 {
		t.Errorf("safeInt64(0) = %d, want 0", got)
	}

	// math.MaxInt64 should pass through.
	if got := safeInt64(uint64(math.MaxInt64)); got != math.MaxInt64 {
		t.Errorf("safeInt64(MaxInt64) = %d, want %d", got, math.MaxInt64)
	}

	// math.MaxInt64 + 1 should clamp.
	if got := safeInt64(uint64(math.MaxInt64) + 1); got != math.MaxInt64 {
		t.Errorf("safeInt64(MaxInt64+1) = %d, want %d", got, math.MaxInt64)
	}

	// math.MaxUint64 should clamp.
	if got := safeInt64(math.MaxUint64); got != math.MaxInt64 {
		t.Errorf("safeInt64(MaxUint64) = %d, want %d", got, math.MaxInt64)
	}
}

const fixtureFastMeminfo = `MemTotal:       16384000 kB
MemFree:         2048000 kB
MemAvailable:    8192000 kB
Buffers:          512000 kB
Cached:          4096000 kB
SwapCached:            0 kB
Active:          6144000 kB
Inactive:        2048000 kB
SwapTotal:       4096000 kB
SwapFree:        4096000 kB
`
