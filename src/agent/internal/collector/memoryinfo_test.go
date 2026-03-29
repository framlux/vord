// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"strings"
	"testing"
)

// fixtureMeminfo32GB is a /proc/meminfo fixture for a 32 GB server with active swap.
const fixtureMeminfo32GB = `MemTotal:       32768000 kB
MemFree:         4096000 kB
MemAvailable:   16384000 kB
Buffers:         1048576 kB
Cached:          8192000 kB
SwapCached:        65536 kB
Active:         12288000 kB
Inactive:        8192000 kB
SwapTotal:       8388608 kB
SwapFree:        6291456 kB
Dirty:               256 kB
Writeback:             0 kB
`

// fixtureNoSwap is a /proc/meminfo fixture for a server with no swap.
const fixtureNoSwap = `MemTotal:       16384000 kB
MemFree:         8192000 kB
MemAvailable:   12288000 kB
SwapTotal:             0 kB
SwapFree:              0 kB
`

func TestMemoryInfoParsing32GB(t *testing.T) {
	meminfo := parseMeminfoData(fixtureMeminfo32GB)

	tests := []struct {
		key      string
		wantKB   int64
		wantDesc string
	}{
		{"MemTotal", 32768000, "32 GB total"},
		{"MemFree", 4096000, "4 GB free"},
		{"MemAvailable", 16384000, "16 GB available"},
		{"SwapTotal", 8388608, "8 GB swap total"},
		{"SwapFree", 6291456, "~6 GB swap free"},
	}

	for _, tt := range tests {
		t.Run(tt.key, func(t *testing.T) {
			got := meminfo[tt.key]
			wantBytes := tt.wantKB * 1024
			if got != wantBytes {
				t.Errorf("%s = %d bytes, want %d bytes (%s)", tt.key, got, wantBytes, tt.wantDesc)
			}
		})
	}
}

func TestMemoryInfoNoSwap(t *testing.T) {
	meminfo := parseMeminfoData(fixtureNoSwap)

	if meminfo["SwapTotal"] != 0 {
		t.Errorf("SwapTotal = %d, want 0", meminfo["SwapTotal"])
	}
	if meminfo["SwapFree"] != 0 {
		t.Errorf("SwapFree = %d, want 0", meminfo["SwapFree"])
	}
}

func TestMemoryInfoPayloadJSON(t *testing.T) {
	payload := memoryInfoPayload{
		MemoryTotal:     32768000 * 1024,
		MemoryFree:      4096000 * 1024,
		MemoryAvailable: 16384000 * 1024,
		SwapTotal:       8388608 * 1024,
		SwapFree:        6291456 * 1024,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded memoryInfoPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.MemoryTotal != payload.MemoryTotal {
		t.Errorf("MemoryTotal = %d, want %d", decoded.MemoryTotal, payload.MemoryTotal)
	}
	if decoded.MemoryFree != payload.MemoryFree {
		t.Errorf("MemoryFree = %d, want %d", decoded.MemoryFree, payload.MemoryFree)
	}
	if decoded.MemoryAvailable != payload.MemoryAvailable {
		t.Errorf("MemoryAvailable = %d, want %d", decoded.MemoryAvailable, payload.MemoryAvailable)
	}
	if decoded.SwapTotal != payload.SwapTotal {
		t.Errorf("SwapTotal = %d, want %d", decoded.SwapTotal, payload.SwapTotal)
	}
	if decoded.SwapFree != payload.SwapFree {
		t.Errorf("SwapFree = %d, want %d", decoded.SwapFree, payload.SwapFree)
	}
}

func TestMemoryInfoPayloadJSONFieldNames(t *testing.T) {
	payload := memoryInfoPayload{
		MemoryTotal:     1000,
		MemoryFree:      500,
		MemoryAvailable: 700,
		SwapTotal:       2000,
		SwapFree:        1500,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	jsonStr := string(data)
	expectedFields := []string{
		"memory_total",
		"memory_free",
		"memory_available",
		"swap_total",
		"swap_free",
	}

	for _, field := range expectedFields {
		if !strings.Contains(jsonStr, field) {
			t.Errorf("JSON output missing field %q: %s", field, jsonStr)
		}
	}
}

func TestMemoryInfoFullPipeline(t *testing.T) {
	// End-to-end: parse fixture, build payload, verify relationships.
	meminfo := parseMeminfoData(fixtureMeminfo32GB)

	payload := memoryInfoPayload{
		MemoryTotal:     meminfo["MemTotal"],
		MemoryFree:      meminfo["MemFree"],
		MemoryAvailable: meminfo["MemAvailable"],
		SwapTotal:       meminfo["SwapTotal"],
		SwapFree:        meminfo["SwapFree"],
	}

	if payload.MemoryTotal <= 0 {
		t.Error("MemoryTotal should be positive")
	}
	if payload.MemoryFree <= 0 {
		t.Error("MemoryFree should be positive")
	}
	if payload.MemoryAvailable <= 0 {
		t.Error("MemoryAvailable should be positive")
	}

	// Available memory should be >= free memory (includes reclaimable cache).
	if payload.MemoryAvailable < payload.MemoryFree {
		t.Errorf("MemoryAvailable (%d) < MemoryFree (%d)", payload.MemoryAvailable, payload.MemoryFree)
	}

	// Available memory should not exceed total.
	if payload.MemoryAvailable > payload.MemoryTotal {
		t.Errorf("MemoryAvailable (%d) > MemoryTotal (%d)", payload.MemoryAvailable, payload.MemoryTotal)
	}

	// Free memory should not exceed total.
	if payload.MemoryFree > payload.MemoryTotal {
		t.Errorf("MemoryFree (%d) > MemoryTotal (%d)", payload.MemoryFree, payload.MemoryTotal)
	}

	// Swap free should not exceed swap total.
	if payload.SwapFree > payload.SwapTotal {
		t.Errorf("SwapFree (%d) > SwapTotal (%d)", payload.SwapFree, payload.SwapTotal)
	}
}

func TestMemTotalBytes(t *testing.T) {
	got := memTotalBytesFromData(fixtureMeminfo32GB)
	want := int64(32768000) * 1024
	if got != want {
		t.Errorf("memTotalBytes = %d, want %d", got, want)
	}
}

func TestMemTotalBytesEmpty(t *testing.T) {
	got := memTotalBytesFromData("")
	if got != 0 {
		t.Errorf("memTotalBytes(empty) = %d, want 0", got)
	}
}

func TestMemTotalBytesMalformed(t *testing.T) {
	got := memTotalBytesFromData("MemTotal: not_a_number kB\n")
	if got != 0 {
		t.Errorf("memTotalBytes(malformed) = %d, want 0", got)
	}
}

func TestMemTotalBytesMissing(t *testing.T) {
	data := `MemFree:         8192000 kB
MemAvailable:   12288000 kB
`
	got := memTotalBytesFromData(data)
	if got != 0 {
		t.Errorf("memTotalBytes(no MemTotal line) = %d, want 0", got)
	}
}
