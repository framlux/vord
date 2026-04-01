// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"math"
	"os"
	"syscall"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/state"
)

// FastTick groups the high-frequency collectors (CpuUsage, MemUsage, DiskUsage)
// into a single goroutine that reads /proc files once per tick and passes the
// data to all parsers.
type FastTick struct {
	store     *db.Store
	rs        *state.RuntimeState
	prevTicks *cpuTicks
}

// NewFastTick creates a new FastTick.
func NewFastTick(store *db.Store, rs *state.RuntimeState) *FastTick {
	return &FastTick{store: store, rs: rs}
}

// Run starts the fast tick loop. It blocks until ctx is cancelled.
func (ft *FastTick) Run(ctx context.Context) {
	interval := ft.rs.TelemetryCollectFastInterval()
	slog.Info("starting fast tick", "interval", interval)

	// Load persisted CPU ticks for crash recovery.
	ft.loadPersistedCpuTicks()

	// Run immediately on startup.
	ft.tick(ctx)

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			slog.Info("stopping fast tick")

			return
		case <-ticker.C:
			ft.tick(ctx)

			if newInterval := ft.rs.TelemetryCollectFastInterval(); newInterval != interval {
				slog.Info("fast tick interval changed", "old", interval, "new", newInterval)
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func (ft *FastTick) tick(ctx context.Context) {
	start := time.Now()

	// Read phase — read all files once.
	statData, statErr := readFileString("/proc/stat")
	meminfoData, meminfoErr := readFileString("/proc/meminfo")
	mountsData, mountsErr := readFileString("/proc/mounts")

	// Parse & enqueue phase — each collector is independent.
	ft.safeCollectCpuUsage(statData, statErr)
	ft.safeCollectMemUsage(meminfoData, meminfoErr)
	ft.safeCollectDiskUsage(ctx, mountsData, mountsErr)

	slog.Debug("fast tick completed", "duration", time.Since(start))
}

// safeCollectCpuUsage wraps collectCpuUsage with panic recovery.
func (ft *FastTick) safeCollectCpuUsage(data string, readErr error) {
	safeRun("cpu_usage", func() { ft.collectCpuUsage(data, readErr) })
}

// safeCollectMemUsage wraps collectMemUsage with panic recovery.
func (ft *FastTick) safeCollectMemUsage(data string, readErr error) {
	safeRun("memory_usage", func() { ft.collectMemUsage(data, readErr) })
}

// safeCollectDiskUsage wraps collectDiskUsage with panic recovery.
func (ft *FastTick) safeCollectDiskUsage(ctx context.Context, data string, readErr error) {
	safeRun("disk_usage", func() { ft.collectDiskUsage(ctx, data, readErr) })
}

func (ft *FastTick) collectCpuUsage(data string, readErr error) {
	if readErr != nil {
		slog.Error("failed to read /proc/stat", "error", readErr)

		return
	}

	current, err := parseCpuTicks(data)
	if err != nil {
		slog.Error("failed to parse cpu ticks", "error", err)

		return
	}

	// Save current ticks for next run.
	ticksJSON, err := json.Marshal(current)
	if err != nil {
		slog.Error("failed to marshal cpu ticks", "error", err)

		return
	}
	stateStr := string(ticksJSON)

	if err := ft.store.SaveCollectorState("cpu_usage", &stateStr); err != nil {
		slog.Error("failed to save cpu ticks state", "error", err)

		return
	}

	// If we have no previous state, skip (first run).
	if ft.prevTicks == nil {
		ft.prevTicks = &current

		return
	}

	totalDelta := current.total() - ft.prevTicks.total()
	if totalDelta <= 0 {
		ft.prevTicks = &current

		return
	}

	idlePct := cpuTickPercent(current.Idle-ft.prevTicks.Idle, totalDelta)
	usagePct := 100 - idlePct

	payload := cpuUsagePayload{
		CPUUsagePercent: usagePct,
		UserTime:        cpuTickPercent(current.User-ft.prevTicks.User, totalDelta),
		SystemTime:      cpuTickPercent(current.System-ft.prevTicks.System, totalDelta),
		NiceTime:        cpuTickPercent(current.Nice-ft.prevTicks.Nice, totalDelta),
		IdleTime:        idlePct,
		IowaitTime:      cpuTickPercent(current.Iowait-ft.prevTicks.Iowait, totalDelta),
		IrqTime:         cpuTickPercent(current.Irq-ft.prevTicks.Irq, totalDelta),
		SoftirqTime:     cpuTickPercent(current.Softirq-ft.prevTicks.Softirq, totalDelta),
		StealTime:       cpuTickPercent(current.Steal-ft.prevTicks.Steal, totalDelta),
	}

	ft.prevTicks = &current

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal cpu usage payload", "error", err)

		return
	}

	if err := ft.store.EnqueueTelemetry(id.NewV7(), db.TelemetryCpuUsage, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue cpu usage telemetry", "error", err)
	}
}

func (ft *FastTick) collectMemUsage(data string, readErr error) {
	if readErr != nil {
		slog.Error("failed to read /proc/meminfo", "error", readErr)

		return
	}

	meminfo := parseMeminfoData(data)

	total := meminfo["MemTotal"]
	available := meminfo["MemAvailable"]
	free := meminfo["MemFree"]
	used := total - available
	pct := memUsagePercent(total, available)

	payload := memUsagePayload{
		MemoryTotal:        total,
		MemoryFree:         free,
		MemoryUsed:         used,
		MemoryUsagePercent: pct,
	}

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal memory usage payload", "error", err)

		return
	}

	if err := ft.store.EnqueueTelemetry(id.NewV7(), db.TelemetryMemoryUsage, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue memory usage telemetry", "error", err)
	}
}

func (ft *FastTick) collectDiskUsage(_ context.Context, data string, readErr error) {
	if readErr != nil {
		slog.Error("failed to read /proc/mounts", "error", readErr)

		return
	}

	mounts := parseMountsData(data)

	var disks []diskUsageEntry
	for _, m := range mounts {
		var stat syscall.Statfs_t
		if err := syscall.Statfs(m.mountPoint, &stat); err != nil {
			continue
		}

		// Use safeInt64 to prevent overflow when casting uint64 block counts on
		// very large filesystems (>8 EiB) where values exceed math.MaxInt64.
		blocks := safeInt64(stat.Blocks)
		blocksFree := safeInt64(stat.Bfree)
		blocksAvail := safeInt64(stat.Bavail)
		blocksUsed := blocks - blocksFree

		disks = append(disks, diskUsageEntry{
			Device:          m.device,
			Path:            m.mountPoint,
			BlocksSize:      int64(stat.Bsize),
			Blocks:          blocks,
			BlocksFree:      blocksFree,
			BlocksAvailable: blocksAvail,
			BlocksUsed:      blocksUsed,
			UsagePercent:    diskUsagePercent(blocks, blocksFree),
		})
	}

	payload := diskUsagePayload{Disks: disks}
	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal disk usage payload", "error", err)

		return
	}

	if err := ft.store.EnqueueTelemetry(id.NewV7(), db.TelemetryDiskUsage, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue disk usage telemetry", "error", err)
	}
}

// loadPersistedCpuTicks loads previous CPU ticks from the collector_state table
// for crash recovery. If the state is missing or corrupted, it starts fresh.
func (ft *FastTick) loadPersistedCpuTicks() {
	state, err := ft.store.GetCollectorState("cpu_usage")
	if err != nil {
		slog.Debug("failed to load persisted cpu ticks", "error", err)

		return
	}
	if state == nil || state.StateJSON == nil {
		return
	}

	var prev cpuTicks
	if err := json.Unmarshal([]byte(*state.StateJSON), &prev); err != nil {
		slog.Debug("failed to unmarshal persisted cpu ticks, starting fresh", "error", err)

		return
	}

	ft.prevTicks = &prev
}

// safeInt64 converts a uint64 to int64, clamping to math.MaxInt64 to prevent
// overflow on very large filesystems where block counts exceed int64 range.
func safeInt64(v uint64) int64 {
	if v > uint64(math.MaxInt64) {
		return math.MaxInt64
	}

	return int64(v)
}

// readFileString reads a file and returns its contents as a string.
func readFileString(path string) (string, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("reading %s: %w", path, err)
	}

	return string(data), nil
}
