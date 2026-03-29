// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"testing"
)

func TestDiskUsagePayloadJSON(t *testing.T) {
	payload := diskUsagePayload{
		Disks: []diskUsageEntry{
			{
				Device:          "/dev/sda1",
				Path:            "/",
				BlocksSize:      4096,
				Blocks:          26214400,
				BlocksFree:      13107200,
				BlocksAvailable: 11796480,
				BlocksUsed:      13107200,
				UsagePercent:    50,
			},
		},
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded diskUsagePayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if len(decoded.Disks) != 1 {
		t.Fatalf("decoded %d disks, want 1", len(decoded.Disks))
	}

	d := decoded.Disks[0]
	if d.Device != "/dev/sda1" {
		t.Errorf("Device = %q, want /dev/sda1", d.Device)
	}
	if d.Path != "/" {
		t.Errorf("Path = %q, want /", d.Path)
	}
	if d.BlocksSize != 4096 {
		t.Errorf("BlocksSize = %d, want 4096", d.BlocksSize)
	}
	if d.UsagePercent != 50 {
		t.Errorf("UsagePercent = %d, want 50", d.UsagePercent)
	}
}

func TestDiskUsagePercentCalculation(t *testing.T) {
	tests := []struct {
		name       string
		blocks     int64
		blocksFree int64
		wantPct    int
	}{
		{"50% used", 1000, 500, 50},
		{"0% used", 1000, 1000, 0},
		{"100% used", 1000, 0, 100},
		{"75% used", 1000, 250, 75},
		{"empty disk", 0, 0, 0},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := diskUsagePercent(tt.blocks, tt.blocksFree)
			if got != tt.wantPct {
				t.Errorf("diskUsagePercent(%d, %d) = %d, want %d", tt.blocks, tt.blocksFree, got, tt.wantPct)
			}
		})
	}
}

func TestDiskUsageEntryFields(t *testing.T) {
	// Verify that the disk usage entry calculation matches the collector logic.
	blocks := int64(26214400)      // ~100 GB at 4096 block size
	blocksFree := int64(13107200)  // ~50 GB free
	blocksAvail := int64(11796480) // ~45 GB available to non-root
	blocksUsed := blocks - blocksFree

	entry := diskUsageEntry{
		Device:          "/dev/nvme0n1p2",
		Path:            "/",
		BlocksSize:      4096,
		Blocks:          blocks,
		BlocksFree:      blocksFree,
		BlocksAvailable: blocksAvail,
		BlocksUsed:      blocksUsed,
		UsagePercent:    diskUsagePercent(blocks, blocksFree),
	}

	if entry.BlocksUsed != 13107200 {
		t.Errorf("BlocksUsed = %d, want 13107200", entry.BlocksUsed)
	}
	if entry.UsagePercent != 50 {
		t.Errorf("UsagePercent = %d, want 50", entry.UsagePercent)
	}

	// BlocksAvailable is typically less than BlocksFree (reserved blocks for root).
	if entry.BlocksAvailable > entry.BlocksFree {
		t.Error("BlocksAvailable should be <= BlocksFree")
	}
}

func TestDiskUsageMultipleDisks(t *testing.T) {
	// Verify payload with multiple disks serializes correctly.
	payload := diskUsagePayload{
		Disks: []diskUsageEntry{
			{Device: "/dev/sda1", Path: "/", BlocksSize: 4096, Blocks: 1000, BlocksFree: 500, BlocksAvailable: 450, BlocksUsed: 500, UsagePercent: 50},
			{Device: "/dev/sdb1", Path: "/data", BlocksSize: 4096, Blocks: 2000, BlocksFree: 200, BlocksAvailable: 150, BlocksUsed: 1800, UsagePercent: 90},
			{Device: "/dev/sdc1", Path: "/backup", BlocksSize: 4096, Blocks: 5000, BlocksFree: 4500, BlocksAvailable: 4400, BlocksUsed: 500, UsagePercent: 10},
		},
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded diskUsagePayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if len(decoded.Disks) != 3 {
		t.Fatalf("decoded %d disks, want 3", len(decoded.Disks))
	}

	// Verify usage percentages are as expected.
	expectedPcts := []int{50, 90, 10}
	for i, expected := range expectedPcts {
		if decoded.Disks[i].UsagePercent != expected {
			t.Errorf("Disks[%d].UsagePercent = %d, want %d", i, decoded.Disks[i].UsagePercent, expected)
		}
	}
}

func TestDiskUsageEmptyDisks(t *testing.T) {
	payload := diskUsagePayload{Disks: nil}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded diskUsagePayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.Disks != nil {
		t.Errorf("expected nil Disks, got %v", decoded.Disks)
	}
}
