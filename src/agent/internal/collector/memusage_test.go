// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Fixture /proc/meminfo content with realistic values from a 16 GB server.
const fixtureMeminfo = `MemTotal:       16384000 kB
MemFree:         2048000 kB
MemAvailable:    8192000 kB
Buffers:          524288 kB
Cached:          4096000 kB
SwapCached:            0 kB
Active:          6144000 kB
Inactive:        3072000 kB
Active(anon):    4096000 kB
Inactive(anon):  1048576 kB
Active(file):    2048000 kB
Inactive(file):  2023424 kB
Unevictable:           0 kB
Mlocked:               0 kB
SwapTotal:       4194304 kB
SwapFree:        4194304 kB
Dirty:               128 kB
Writeback:             0 kB
AnonPages:       5120000 kB
Mapped:           524288 kB
Shmem:            131072 kB
KReclaimable:     524288 kB
Slab:             786432 kB
SReclaimable:     524288 kB
SUnreclaim:       262144 kB
KernelStack:       16384 kB
PageTables:        32768 kB
NFS_Unstable:          0 kB
Bounce:                0 kB
WritebackTmp:          0 kB
CommitLimit:    12386304 kB
Committed_AS:    8388608 kB
VmallocTotal:   34359738367 kB
VmallocUsed:       65536 kB
VmallocChunk:          0 kB
HardwareCorrupted:     0 kB
AnonHugePages:         0 kB
ShmemHugePages:        0 kB
ShmemPmdMapped:        0 kB
HugePages_Total:       0
HugePages_Free:        0
HugePages_Rsvd:        0
HugePages_Surp:        0
Hugepagesize:       2048 kB
DirectMap4k:      262144 kB
DirectMap2M:    12582912 kB
DirectMap1G:     4194304 kB
`

// Fixture with zero MemTotal to test division-by-zero protection.
const fixtureMeminfoZeroTotal = `MemTotal:              0 kB
MemFree:               0 kB
MemAvailable:          0 kB
SwapTotal:             0 kB
SwapFree:              0 kB
`

func TestParseMeminfoFromFixture(t *testing.T) {
	meminfo := parseMeminfoData(fixtureMeminfo)

	tests := []struct {
		key      string
		wantKB   int64
		wantDesc string
	}{
		{"MemTotal", 16384000, "16 GB total"},
		{"MemFree", 2048000, "2 GB free"},
		{"MemAvailable", 8192000, "8 GB available"},
		{"SwapTotal", 4194304, "4 GB swap total"},
		{"SwapFree", 4194304, "4 GB swap free"},
		{"Buffers", 524288, "512 MB buffers"},
		{"Cached", 4096000, "4 GB cached"},
	}

	for _, tt := range tests {
		t.Run(tt.key, func(t *testing.T) {
			got, ok := meminfo[tt.key]
			if !ok {
				t.Fatalf("key %q not found in parsed meminfo", tt.key)
			}
			wantBytes := tt.wantKB * 1024
			if got != wantBytes {
				t.Errorf("%s = %d bytes, want %d bytes (%s)", tt.key, got, wantBytes, tt.wantDesc)
			}
		})
	}
}

func TestParseMeminfoEmpty(t *testing.T) {
	meminfo := parseMeminfoData("")
	if len(meminfo) != 0 {
		t.Errorf("empty input should produce empty map, got %d entries", len(meminfo))
	}
}

func TestParseMeminfoMalformedLines(t *testing.T) {
	data := `this line has no colon
MemTotal: not_a_number kB
MemFree:         2048000 kB
`
	meminfo := parseMeminfoData(data)
	// Only MemFree should parse successfully.
	if _, ok := meminfo["MemTotal"]; ok {
		t.Error("expected MemTotal to be absent due to parse failure")
	}
	if _, ok := meminfo["MemFree"]; !ok {
		t.Error("expected MemFree to be present")
	}
}

func TestMemUsagePercent(t *testing.T) {
	tests := []struct {
		name      string
		total     int64
		available int64
		wantPct   int
	}{
		{"50% used", 1000, 500, 50},
		{"0% used", 1000, 1000, 0},
		{"100% used", 1000, 0, 100},
		{"75% used", 1000, 250, 75},
		{"zero total", 0, 0, 0},
		{"negative total", -1, 0, 0},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := memUsagePercent(tt.total, tt.available)
			if got != tt.wantPct {
				t.Errorf("memUsagePercent(%d, %d) = %d, want %d", tt.total, tt.available, got, tt.wantPct)
			}
		})
	}
}

func TestMemUsagePercentWithFixture(t *testing.T) {
	meminfo := parseMeminfoData(fixtureMeminfo)

	total := meminfo["MemTotal"]
	available := meminfo["MemAvailable"]

	// 16384000 kB total, 8192000 kB available → 50% used (both converted to bytes).
	pct := memUsagePercent(total, available)
	if pct != 50 {
		t.Errorf("usage percent = %d, want 50", pct)
	}
}

func TestMemUsagePercentZeroTotal(t *testing.T) {
	meminfo := parseMeminfoData(fixtureMeminfoZeroTotal)

	total := meminfo["MemTotal"]
	available := meminfo["MemAvailable"]

	pct := memUsagePercent(total, available)
	if pct != 0 {
		t.Errorf("usage percent with zero total = %d, want 0", pct)
	}
}

func TestMemUsagePayloadJSON(t *testing.T) {
	payload := memUsagePayload{
		MemoryTotal:        16384000 * 1024,
		MemoryFree:         2048000 * 1024,
		MemoryUsed:         8192000 * 1024,
		MemoryUsagePercent: 50,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded memUsagePayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.MemoryUsagePercent != 50 {
		t.Errorf("MemoryUsagePercent = %d, want 50", decoded.MemoryUsagePercent)
	}
	if decoded.MemoryTotal != payload.MemoryTotal {
		t.Errorf("MemoryTotal = %d, want %d", decoded.MemoryTotal, payload.MemoryTotal)
	}
}

func TestMemUsageFullPipeline(t *testing.T) {
	// Simulate the full collection logic with fixture data.
	meminfo := parseMeminfoData(fixtureMeminfo)

	total := meminfo["MemTotal"]
	available := meminfo["MemAvailable"]
	free := meminfo["MemFree"]
	used := total - available

	payload := memUsagePayload{
		MemoryTotal:        total,
		MemoryFree:         free,
		MemoryUsed:         used,
		MemoryUsagePercent: memUsagePercent(total, available),
	}

	if payload.MemoryTotal <= 0 {
		t.Error("MemoryTotal should be positive")
	}
	if payload.MemoryFree <= 0 {
		t.Error("MemoryFree should be positive")
	}
	if payload.MemoryUsed <= 0 {
		t.Error("MemoryUsed should be positive")
	}
	if payload.MemoryUsagePercent < 0 || payload.MemoryUsagePercent > 100 {
		t.Errorf("MemoryUsagePercent = %d, want 0-100", payload.MemoryUsagePercent)
	}
	// MemoryUsed is total-available, MemoryFree is the raw free amount.
	// They don't sum to total because buffers/cache make up the difference.
	// Verify each is individually within range of total.
	if payload.MemoryUsed > payload.MemoryTotal {
		t.Errorf("MemoryUsed (%d) > MemoryTotal (%d)", payload.MemoryUsed, payload.MemoryTotal)
	}
	if payload.MemoryFree > payload.MemoryTotal {
		t.Errorf("MemoryFree (%d) > MemoryTotal (%d)", payload.MemoryFree, payload.MemoryTotal)
	}
}

func TestReadFileTrimmed(t *testing.T) {
	dir := t.TempDir()

	t.Run("normal file", func(t *testing.T) {
		path := filepath.Join(dir, "test.txt")
		if err := os.WriteFile(path, []byte("  hello world  \n"), 0644); err != nil {
			t.Fatal(err)
		}
		got := readFileTrimmed(path)
		if got != "hello world" {
			t.Errorf("readFileTrimmed() = %q, want %q", got, "hello world")
		}
	})

	t.Run("nonexistent file", func(t *testing.T) {
		got := readFileTrimmed(filepath.Join(dir, "nonexistent"))
		if got != "" {
			t.Errorf("readFileTrimmed(nonexistent) = %q, want empty string", got)
		}
	})

	t.Run("empty file", func(t *testing.T) {
		path := filepath.Join(dir, "empty.txt")
		if err := os.WriteFile(path, []byte(""), 0644); err != nil {
			t.Fatal(err)
		}
		got := readFileTrimmed(path)
		if got != "" {
			t.Errorf("readFileTrimmed(empty) = %q, want empty string", got)
		}
	})

	t.Run("newlines only", func(t *testing.T) {
		path := filepath.Join(dir, "newlines.txt")
		if err := os.WriteFile(path, []byte("\n\n\n"), 0644); err != nil {
			t.Fatal(err)
		}
		got := readFileTrimmed(path)
		if got != "" {
			t.Errorf("readFileTrimmed(newlines) = %q, want empty string", got)
		}
	})
}
