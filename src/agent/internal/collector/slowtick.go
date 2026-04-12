// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"log/slog"
	"os"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/state"
)

const hourlyTickInterval = 4 // every 4th slow tick = 1 hour

// SlowTick groups the low-frequency collectors (MemoryInfo, DiskInfo, and
// hourly SystemInfo, OsVersion, CpuInfo) into a single goroutine. It reads
// /proc files once per tick and passes the data to all parsers.
type SlowTick struct {
	store   *db.Store
	rs      *state.RuntimeState
	tickNum int
}

// NewSlowTick creates a new SlowTick.
func NewSlowTick(store *db.Store, rs *state.RuntimeState) *SlowTick {
	return &SlowTick{store: store, rs: rs}
}

// Run starts the slow tick loop. It blocks until ctx is cancelled.
func (st *SlowTick) Run(ctx context.Context) {
	interval := st.rs.TelemetryCollectSlowInterval()
	slog.Info("starting slow tick", "interval", interval.String())

	// Run immediately on startup (tick 0 is an hourly tick).
	st.tick(ctx)

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			slog.Info("stopping slow tick")

			return
		case <-ticker.C:
			st.tick(ctx)

			if newInterval := st.rs.TelemetryCollectSlowInterval(); newInterval != interval {
				slog.Info("slow tick interval changed", "old", interval.String(), "new", newInterval.String())
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func (st *SlowTick) tick(ctx context.Context) {
	start := time.Now()
	isHourly := st.tickNum%hourlyTickInterval == 0

	// Read phase — read files needed for this tick.
	meminfoData, meminfoErr := readFileString("/proc/meminfo")
	mountsData, mountsErr := readFileString("/proc/mounts")

	var cpuinfoData, uptimeData, osReleaseData string
	var cpuinfoErr, uptimeErr, osReleaseErr error
	if isHourly {
		cpuinfoData, cpuinfoErr = readFileString("/proc/cpuinfo")
		uptimeData, uptimeErr = readFileString("/proc/uptime")
		osReleaseData, osReleaseErr = readFileString("/etc/os-release")
	}

	// Parse & enqueue phase — every tick.
	st.safeCollectMemoryInfo(meminfoData, meminfoErr)
	st.safeCollectDiskInfo(ctx, mountsData, mountsErr)

	// Parse & enqueue phase — hourly.
	if isHourly {
		st.safeCollectSystemInfo(ctx, cpuinfoData, cpuinfoErr, meminfoData, meminfoErr, uptimeData, uptimeErr)
		st.safeCollectOsVersion(ctx, osReleaseData, osReleaseErr)
		st.safeCollectCpuInfo(cpuinfoData, cpuinfoErr)
	}

	st.tickNum++
	slog.Debug("slow tick completed", "tick", st.tickNum, "hourly", isHourly, "duration", time.Since(start))
}

// safeCollectMemoryInfo wraps collectMemoryInfo with panic recovery.
func (st *SlowTick) safeCollectMemoryInfo(data string, readErr error) {
	safeRun("memory_info", func() { st.collectMemoryInfo(data, readErr) })
}

// safeCollectDiskInfo wraps collectDiskInfo with panic recovery.
func (st *SlowTick) safeCollectDiskInfo(ctx context.Context, data string, readErr error) {
	safeRun("disk_info", func() { st.collectDiskInfo(ctx, data, readErr) })
}

// safeCollectSystemInfo wraps collectSystemInfo with panic recovery.
func (st *SlowTick) safeCollectSystemInfo(ctx context.Context, cpuinfoData string, cpuinfoErr error, meminfoData string, meminfoErr error, uptimeData string, uptimeErr error) {
	safeRun("system_info", func() {
		st.collectSystemInfo(ctx, cpuinfoData, cpuinfoErr, meminfoData, meminfoErr, uptimeData, uptimeErr)
	})
}

// safeCollectOsVersion wraps collectOsVersion with panic recovery.
func (st *SlowTick) safeCollectOsVersion(ctx context.Context, data string, readErr error) {
	safeRun("os_version", func() { st.collectOsVersion(ctx, data, readErr) })
}

// safeCollectCpuInfo wraps collectCpuInfo with panic recovery.
func (st *SlowTick) safeCollectCpuInfo(data string, readErr error) {
	safeRun("cpu_info", func() { st.collectCpuInfo(data, readErr) })
}

func (st *SlowTick) collectMemoryInfo(data string, readErr error) {
	if readErr != nil {
		slog.Error("failed to read /proc/meminfo", "error", readErr)

		return
	}

	meminfo := parseMeminfoData(data)

	payload := memoryInfoPayload{
		MemoryTotal:     meminfo["MemTotal"],
		MemoryFree:      meminfo["MemFree"],
		MemoryAvailable: meminfo["MemAvailable"],
		SwapTotal:       meminfo["SwapTotal"],
		SwapFree:        meminfo["SwapFree"],
	}

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal memory info payload", "error", err)

		return
	}

	if err := st.store.EnqueueTelemetry(id.NewV7(), db.TelemetryMemoryInfo, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue memory info telemetry", "error", err)
	}
}

func (st *SlowTick) collectDiskInfo(_ context.Context, data string, readErr error) {
	if readErr != nil {
		slog.Error("failed to read /proc/mounts", "error", readErr)

		return
	}

	mounts := parseMountsData(data)

	var disks []diskInfoEntry
	for _, m := range mounts {
		var stat syscall.Statfs_t
		if err := syscall.Statfs(m.mountPoint, &stat); err != nil {
			continue
		}

		totalBytes := safeInt64(stat.Blocks) * int64(stat.Bsize)
		freeBytes := safeInt64(stat.Bavail) * int64(stat.Bsize)
		usedBytes := totalBytes - (safeInt64(stat.Bfree) * int64(stat.Bsize))

		disks = append(disks, diskInfoEntry{
			Device:         m.device,
			MountPoint:     m.mountPoint,
			FSType:         m.fsType,
			TotalBytes:     totalBytes,
			UsedBytes:      usedBytes,
			AvailableBytes: freeBytes,
			PercentUsed:    diskPercentUsed(totalBytes, usedBytes),
		})
	}

	payload := diskInfoPayload{Disks: disks}
	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal disk info payload", "error", err)

		return
	}

	if err := st.store.EnqueueTelemetry(id.NewV7(), db.TelemetryDiskInfo, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue disk info telemetry", "error", err)
	}
}

func (st *SlowTick) collectSystemInfo(ctx context.Context, cpuinfoData string, cpuinfoErr error, meminfoData string, meminfoErr error, uptimeData string, uptimeErr error) {
	hostname, _ := os.Hostname()

	cpuBrand := ""
	if cpuinfoErr == nil {
		cpuBrand = cpuBrandFromData(cpuinfoData)
	}

	physicalCores := runtime.NumCPU()
	if cpuinfoErr == nil {
		count := countPhysicalCoresFromData(cpuinfoData)
		if count > 0 {
			physicalCores = count
		}
	}

	var memTotal int64
	if meminfoErr == nil {
		memTotal = memTotalBytesFromData(meminfoData)
	}

	var uptime int64
	if uptimeErr == nil {
		uptime = uptimeSecondsFromData(uptimeData)
	}

	payload := systemInfoPayload{
		Hostname:         hostname,
		UUID:             readFileTrimmed("/etc/machine-id"),
		CPUType:          runtime.GOARCH,
		CPUBrand:         cpuBrand,
		CPUPhysicalCores: physicalCores,
		CPULogicalCores:  runtime.NumCPU(),
		PhysicalMemory:   memTotal,
		HardwareVendor:   readDMI("sys_vendor"),
		HardwareModel:    readDMI("product_name"),
		HardwareVersion:  readDMI("product_version"),
		HardwareSerial:   hardwareSerial(ctx),
		BoardVendor:      readDMI("board_vendor"),
		BoardModel:       readDMI("board_name"),
		BoardVersion:     readDMI("board_version"),
		BoardSerial:      readDMI("board_serial"),
		ComputerName:     hostname,
		LocalHostname:    hostname,
		UptimeSeconds:    uptime,
		BiosVersion:      readDMI("bios_version"),
		IpAddresses:      globalIPAddresses(ctx),
	}

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal system info payload", "error", err)

		return
	}

	if err := st.store.EnqueueTelemetry(id.NewV7(), db.TelemetrySystemInfo, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue system info telemetry", "error", err)
	}
}

func (st *SlowTick) collectOsVersion(ctx context.Context, data string, readErr error) {
	var osRelease map[string]string
	if readErr != nil {
		slog.Error("failed to read /etc/os-release", "error", readErr)
		osRelease = make(map[string]string)
	} else {
		osRelease = parseOsReleaseData(data)
	}

	version := osRelease["VERSION_ID"]
	major, minor, patch := parseVersion(version)

	kernelVersion := ""
	if out, err := runCmd(ctx, "uname", "-r"); err == nil {
		kernelVersion = strings.TrimSpace(string(out))
	}

	payload := osVersionPayload{
		Name:     osRelease["NAME"],
		Version:  version,
		Major:    major,
		Minor:    minor,
		Patch:    patch,
		Build:    kernelVersion,
		Platform: osRelease["ID"],
		Codename: osRelease["VERSION_CODENAME"],
		Arch:     runtime.GOARCH,
		Extra:    readFileTrimmed("/proc/version"),
		Revision: osRelease["BUILD_ID"],
	}

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal os version payload", "error", err)

		return
	}

	if err := st.store.EnqueueTelemetry(id.NewV7(), db.TelemetryOsVersion, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue os version telemetry", "error", err)
	}
}

func (st *SlowTick) collectCpuInfo(data string, readErr error) {
	var cpuinfo map[string]string
	if readErr != nil {
		slog.Error("failed to read /proc/cpuinfo", "error", readErr)
		cpuinfo = make(map[string]string)
	} else {
		cpuinfo = parseProcCpuinfoData(data)
	}

	addressWidth := "64"
	if runtime.GOARCH == "386" || runtime.GOARCH == "arm" {
		addressWidth = "32"
	}

	var currentMhz int
	if mhzStr := cpuinfo["cpu MHz"]; mhzStr != "" {
		currentMhz, _ = strconv.Atoi(strings.Split(mhzStr, ".")[0])
	}

	physicalCores := 0
	if readErr == nil {
		physicalCores = countPhysicalCoresFromData(data)
	}
	if physicalCores == 0 {
		physicalCores = runtime.NumCPU()
	}

	payload := cpuInfoPayload{
		DeviceID:          "CPU0",
		Model:             cpuinfo["model name"],
		Manufacturer:      cpuinfo["vendor_id"],
		ProcessorType:     runtime.GOARCH,
		CPUStatus:         1, // enabled
		NumberOfCores:     strconv.Itoa(physicalCores),
		LogicalProcessors: runtime.NumCPU(),
		AddressWidth:      addressWidth,
		CurrentClockSpeed: currentMhz,
		MaxClockSpeed:     currentMhz, // best available from /proc/cpuinfo
		SocketDesignation: cpuinfo["physical id"],
	}

	payloadJSON, err := json.Marshal(payload)
	if err != nil {
		slog.Error("failed to marshal cpu info payload", "error", err)

		return
	}

	if err := st.store.EnqueueTelemetry(id.NewV7(), db.TelemetryCpuInfo, string(payloadJSON)); err != nil {
		slog.Error("failed to enqueue cpu info telemetry", "error", err)
	}
}
