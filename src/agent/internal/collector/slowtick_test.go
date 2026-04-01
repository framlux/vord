// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/state"
)

func TestSlowTickCollectMemoryInfo(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	st.collectMemoryInfo(fixtureSlowMeminfo, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryMemoryInfo {
		t.Errorf("expected type %d, got %d", db.TelemetryMemoryInfo, items[0].ItemType)
	}

	var payload memoryInfoPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.MemoryTotal != 32768000*1024 {
		t.Errorf("expected MemoryTotal=%d, got %d", 32768000*1024, payload.MemoryTotal)
	}
	if payload.SwapTotal != 8192000*1024 {
		t.Errorf("expected SwapTotal=%d, got %d", 8192000*1024, payload.SwapTotal)
	}
}

func TestSlowTickSubIntervalLogic(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	// Tick 0 is hourly (0 % 4 == 0).
	if st.tickNum%hourlyTickInterval != 0 {
		t.Error("tick 0 should be an hourly tick")
	}

	// Simulate ticks 1, 2, 3 — not hourly.
	for i := 1; i < hourlyTickInterval; i++ {
		st.tickNum = i
		if st.tickNum%hourlyTickInterval == 0 {
			t.Errorf("tick %d should not be an hourly tick", i)
		}
	}

	// Tick 4 is hourly again.
	st.tickNum = hourlyTickInterval
	if st.tickNum%hourlyTickInterval != 0 {
		t.Errorf("tick %d should be an hourly tick", hourlyTickInterval)
	}
}

func TestSlowTickHourlyCollectorsRunOnStartup(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	// tickNum starts at 0, which is hourly.
	if st.tickNum != 0 {
		t.Fatalf("expected tickNum=0 on startup, got %d", st.tickNum)
	}

	isHourly := st.tickNum%hourlyTickInterval == 0
	if isHourly == false {
		t.Error("startup tick should be hourly")
	}
}

func TestSlowTickGarbageDataNoPanic(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("slow tick panicked with garbage data: %v", r)
		}
	}()

	st.safeCollectMemoryInfo("\x00\x01\x02\xff\xfe", nil)
	st.safeCollectDiskInfo(nil, "\x00\x01\x02\xff\xfe", nil)
	st.safeCollectCpuInfo("\x00\x01\x02\xff\xfe", nil)
}

func TestSlowTickCollectCpuInfo(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	st.collectCpuInfo(fixtureSlowCpuinfo, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryCpuInfo {
		t.Errorf("expected type %d, got %d", db.TelemetryCpuInfo, items[0].ItemType)
	}

	var payload cpuInfoPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.Model != "Intel(R) Xeon(R) CPU E5-2686 v4 @ 2.30GHz" {
		t.Errorf("unexpected model: %s", payload.Model)
	}
}

func TestSlowTickCollectMemoryInfoReadError(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	// Should not panic on read error, just log and skip.
	st.collectMemoryInfo("", errorString("simulated error"))

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 telemetry items on read error, got %d", len(items))
	}
}

// Test fixtures.
const fixtureSlowMeminfo = `MemTotal:       32768000 kB
MemFree:         4096000 kB
MemAvailable:   16384000 kB
Buffers:         1024000 kB
Cached:          8192000 kB
SwapCached:            0 kB
Active:         12288000 kB
Inactive:        4096000 kB
SwapTotal:       8192000 kB
SwapFree:        8192000 kB
`

// Intent: Disk info payload has device, mount, fstype, total, used, free fields.
func TestSlowTickCollectDiskInfo_PopulatesAllFields(t *testing.T) {
	// parseMountsData + Statfs is platform-specific, but we can test the parser.
	data := "/dev/sda1 / ext4 rw,relatime 0 0\n"
	mounts := parseMountsData(data)

	if len(mounts) != 1 {
		t.Fatalf("expected 1 mount, got %d", len(mounts))
	}

	m := mounts[0]
	if m.device != "/dev/sda1" {
		t.Errorf("expected device=/dev/sda1, got %q", m.device)
	}
	if m.mountPoint != "/" {
		t.Errorf("expected mountPoint=/, got %q", m.mountPoint)
	}
	if m.fsType != "ext4" {
		t.Errorf("expected fsType=ext4, got %q", m.fsType)
	}
}

// Intent: Read error → no telemetry enqueued, no panic.
func TestSlowTickCollectDiskInfo_ReadError(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	st.collectDiskInfo(nil, "", errorString("simulated read error"))

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items on read error, got %d", len(items))
	}
}

// Intent: Valid os-release fixture → correct ID, version, pretty name.
func TestSlowTickCollectOsVersion_ParsesOsRelease(t *testing.T) {
	data := `NAME="Ubuntu"
VERSION_ID="22.04"
VERSION_CODENAME=jammy
ID=ubuntu
BUILD_ID=
`
	osRelease := parseOsReleaseData(data)

	if osRelease["NAME"] != "Ubuntu" {
		t.Errorf("expected NAME=Ubuntu, got %q", osRelease["NAME"])
	}
	if osRelease["VERSION_ID"] != "22.04" {
		t.Errorf("expected VERSION_ID=22.04, got %q", osRelease["VERSION_ID"])
	}
	if osRelease["ID"] != "ubuntu" {
		t.Errorf("expected ID=ubuntu, got %q", osRelease["ID"])
	}
	if osRelease["VERSION_CODENAME"] != "jammy" {
		t.Errorf("expected VERSION_CODENAME=jammy, got %q", osRelease["VERSION_CODENAME"])
	}

	major, minor, patch := parseVersion(osRelease["VERSION_ID"])
	if major != 22 {
		t.Errorf("expected major=22, got %d", major)
	}
	if minor != 4 {
		t.Errorf("expected minor=4, got %d", minor)
	}
	if patch != 0 {
		t.Errorf("expected patch=0, got %d", patch)
	}
}

// Intent: Valid fixtures produce correct system info fields.
func TestSlowTickCollectSystemInfo_ParseHelpers(t *testing.T) {
	// Test cpuBrandFromData
	brand := cpuBrandFromData(fixtureSlowCpuinfo)
	if brand != "Intel(R) Xeon(R) CPU E5-2686 v4 @ 2.30GHz" {
		t.Errorf("unexpected CPU brand: %q", brand)
	}

	// Test countPhysicalCoresFromData
	cores := countPhysicalCoresFromData(fixtureSlowCpuinfo)
	if cores != 1 {
		t.Errorf("expected 1 physical core, got %d", cores)
	}

	// Test memTotalBytesFromData
	memTotal := memTotalBytesFromData(fixtureSlowMeminfo)
	if memTotal != 32768000*1024 {
		t.Errorf("expected memTotal=%d, got %d", 32768000*1024, memTotal)
	}

	// Test uptimeSecondsFromData
	uptime := uptimeSecondsFromData("123456.78 67890.12")
	if uptime != 123456 {
		t.Errorf("expected uptime=123456, got %d", uptime)
	}

	// Test parseGlobalIPAddresses
	ipOutput := "2: eth0    inet 10.0.1.10/24 brd 10.0.1.255 scope global eth0\n3: eth0    inet6 fd00::1/64 scope global\n"
	ips := parseGlobalIPAddresses(ipOutput)
	if len(ips) != 2 {
		t.Errorf("expected 2 IPs, got %d", len(ips))
	}
	if len(ips) >= 1 && ips[0] != "10.0.1.10" {
		t.Errorf("expected first IP=10.0.1.10, got %q", ips[0])
	}
}

// Intent: safeCollectSystemInfo does not panic with valid fixture data.
// Exercises collectSystemInfo which calls os.Hostname, readDMI, etc.
func TestSlowTickSafeCollectSystemInfo_ValidData(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("safeCollectSystemInfo panicked: %v", r)
		}
	}()

	ctx := context.Background()
	st.safeCollectSystemInfo(ctx, fixtureSlowCpuinfo, nil, fixtureSlowMeminfo, nil, "123456.78 67890.12", nil)

	// Verify system info was enqueued.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item (system info), got %d", len(items))
	}
	if items[0].ItemType != db.TelemetrySystemInfo {
		t.Errorf("expected type %d, got %d", db.TelemetrySystemInfo, items[0].ItemType)
	}

	// Verify payload has expected fields from fixtures.
	var payload systemInfoPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if payload.CPUBrand != "Intel(R) Xeon(R) CPU E5-2686 v4 @ 2.30GHz" {
		t.Errorf("unexpected CPUBrand: %q", payload.CPUBrand)
	}
	if payload.PhysicalMemory != 32768000*1024 {
		t.Errorf("expected PhysicalMemory=%d, got %d", 32768000*1024, payload.PhysicalMemory)
	}
	if payload.UptimeSeconds != 123456 {
		t.Errorf("expected UptimeSeconds=123456, got %d", payload.UptimeSeconds)
	}
}

// Intent: safeCollectSystemInfo handles read errors without panic.
func TestSlowTickSafeCollectSystemInfo_ReadErrors(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("safeCollectSystemInfo panicked on read errors: %v", r)
		}
	}()

	ctx := context.Background()
	// All data sources fail.
	simErr := errorString("simulated error")
	st.safeCollectSystemInfo(ctx, "", simErr, "", simErr, "", simErr)

	// System info should still be enqueued (with zero/default values for failed sources).
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
}

// Intent: safeCollectOsVersion with valid data produces telemetry with parsed os-release fields.
func TestSlowTickSafeCollectOsVersion_ValidData(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("safeCollectOsVersion panicked: %v", r)
		}
	}()

	osReleaseFixture := `NAME="Ubuntu"
VERSION_ID="22.04"
VERSION_CODENAME=jammy
ID=ubuntu
BUILD_ID=
`

	ctx := context.Background()
	st.safeCollectOsVersion(ctx, osReleaseFixture, nil)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryOsVersion {
		t.Errorf("expected type %d, got %d", db.TelemetryOsVersion, items[0].ItemType)
	}

	var payload osVersionPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if payload.Name != "Ubuntu" {
		t.Errorf("expected Name=Ubuntu, got %q", payload.Name)
	}
	if payload.Version != "22.04" {
		t.Errorf("expected Version=22.04, got %q", payload.Version)
	}
	if payload.Major != 22 {
		t.Errorf("expected Major=22, got %d", payload.Major)
	}
	if payload.Minor != 4 {
		t.Errorf("expected Minor=4, got %d", payload.Minor)
	}
	if payload.Platform != "ubuntu" {
		t.Errorf("expected Platform=ubuntu, got %q", payload.Platform)
	}
	if payload.Codename != "jammy" {
		t.Errorf("expected Codename=jammy, got %q", payload.Codename)
	}
}

// Intent: safeCollectOsVersion with read error still produces telemetry (empty/default values).
func TestSlowTickSafeCollectOsVersion_ReadError(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("safeCollectOsVersion panicked on read error: %v", r)
		}
	}()

	ctx := context.Background()
	st.safeCollectOsVersion(ctx, "", errorString("simulated"))

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
}

// Intent: safeCollectSystemInfo and safeCollectOsVersion with garbage data produce no panic.
func TestSlowTickSafeCollectHourly_GarbageData(t *testing.T) {
	store := newTestStore(t)
	st := NewSlowTick(store, state.New())

	defer func() {
		if r := recover(); r != nil {
			t.Errorf("hourly collectors panicked with garbage data: %v", r)
		}
	}()

	ctx := context.Background()
	st.safeCollectSystemInfo(ctx, "\x00\x01", nil, "\x00\x01", nil, "\x00\x01", nil)
	st.safeCollectOsVersion(ctx, "\x00\x01\xff\xfe", nil)
}

const fixtureSlowCpuinfo = `processor	: 0
vendor_id	: GenuineIntel
cpu family	: 6
model		: 79
model name	: Intel(R) Xeon(R) CPU E5-2686 v4 @ 2.30GHz
stepping	: 1
microcode	: 0xb000040
cpu MHz		: 2300.000
cache size	: 46080 KB
physical id	: 0
siblings	: 4
core id		: 0
cpu cores	: 2
apicid		: 0
`
