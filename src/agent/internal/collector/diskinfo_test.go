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

// Fixture /proc/mounts content with a mix of real and pseudo filesystems.
const fixtureProcMounts = `/dev/sda1 / ext4 rw,relatime 0 0
/dev/sda2 /boot ext4 rw,relatime 0 0
/dev/sdb1 /data xfs rw,relatime 0 0
proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
tmpfs /run tmpfs rw,nosuid,nodev 0 0
devtmpfs /dev devtmpfs rw,nosuid 0 0
devpts /dev/pts devpts rw,nosuid,noexec,relatime 0 0
cgroup2 /sys/fs/cgroup cgroup2 rw,nosuid,nodev,noexec,relatime 0 0
/dev/sdc1 /mnt/backup btrfs rw,relatime 0 0
`

// Fixture with duplicate mount points.
const fixtureProcMountsDuplicates = `/dev/sda1 / ext4 rw,relatime 0 0
/dev/sdb1 / ext4 rw,relatime 0 0
/dev/sdc1 /data xfs rw,relatime 0 0
`

// Fixture with only pseudo filesystems.
const fixtureProcMountsOnlyPseudo = `proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0
sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0
tmpfs /run tmpfs rw,nosuid,nodev 0 0
devtmpfs /dev devtmpfs rw,nosuid 0 0
`

// Fixture with short lines (fewer than 3 fields).
const fixtureProcMountsShortLines = `/dev/sda1 /
just two
/dev/sdb1 /data xfs rw,relatime 0 0
`

func TestPseudoFSEntries(t *testing.T) {
	// Verify that common pseudo filesystems are in the exclusion map.
	expectedPseudo := []string{
		"proc", "sysfs", "tmpfs", "devtmpfs", "devpts",
		"cgroup", "cgroup2", "pstore", "securityfs", "debugfs",
		"configfs", "fusectl", "mqueue", "hugetlbfs", "autofs",
		"binfmt_misc", "tracefs", "efivarfs", "bpf", "nsfs",
		"overlay", "squashfs", "ramfs", "rpc_pipefs", "nfsd",
		"sunrpc", "fuse.gvfsd-fuse",
	}

	for _, fs := range expectedPseudo {
		if !pseudoFS[fs] {
			t.Errorf("pseudoFS[%q] = false, want true", fs)
		}
	}
}

func TestPseudoFSRealFilesystems(t *testing.T) {
	// Verify that real filesystems are NOT in the exclusion map.
	realFS := []string{"ext4", "xfs", "btrfs", "ext3", "zfs", "ntfs", "vfat"}

	for _, fs := range realFS {
		if pseudoFS[fs] {
			t.Errorf("pseudoFS[%q] = true, want false (real filesystem should not be excluded)", fs)
		}
	}
}

func TestParseMountsFromFixture(t *testing.T) {
	entries := parseMountsData(fixtureProcMounts)

	// Should only include real filesystem mounts: /, /boot, /data, /mnt/backup.
	if len(entries) != 4 {
		t.Fatalf("got %d entries, want 4", len(entries))
	}

	expected := []struct {
		device     string
		mountPoint string
		fsType     string
	}{
		{"/dev/sda1", "/", "ext4"},
		{"/dev/sda2", "/boot", "ext4"},
		{"/dev/sdb1", "/data", "xfs"},
		{"/dev/sdc1", "/mnt/backup", "btrfs"},
	}

	for i, exp := range expected {
		if entries[i].device != exp.device {
			t.Errorf("entry[%d].device = %q, want %q", i, entries[i].device, exp.device)
		}
		if entries[i].mountPoint != exp.mountPoint {
			t.Errorf("entry[%d].mountPoint = %q, want %q", i, entries[i].mountPoint, exp.mountPoint)
		}
		if entries[i].fsType != exp.fsType {
			t.Errorf("entry[%d].fsType = %q, want %q", i, entries[i].fsType, exp.fsType)
		}
	}
}

func TestParseMountsDeduplicate(t *testing.T) {
	entries := parseMountsData(fixtureProcMountsDuplicates)

	// Two entries mount on /, but only the first should be kept.
	if len(entries) != 2 {
		t.Fatalf("got %d entries, want 2 (deduplicated)", len(entries))
	}

	// First entry should be /dev/sda1 on /.
	if entries[0].device != "/dev/sda1" {
		t.Errorf("first entry device = %q, want /dev/sda1", entries[0].device)
	}
	if entries[1].mountPoint != "/data" {
		t.Errorf("second entry mountPoint = %q, want /data", entries[1].mountPoint)
	}
}

func TestParseMountsOnlyPseudo(t *testing.T) {
	entries := parseMountsData(fixtureProcMountsOnlyPseudo)

	if len(entries) != 0 {
		t.Errorf("got %d entries, want 0 (all pseudo filesystems)", len(entries))
	}
}

func TestParseMountsEmpty(t *testing.T) {
	entries := parseMountsData("")

	if len(entries) != 0 {
		t.Errorf("got %d entries from empty input, want 0", len(entries))
	}
}

func TestParseMountsShortLines(t *testing.T) {
	entries := parseMountsData(fixtureProcMountsShortLines)

	// Only the last line has 3+ fields and is a real filesystem.
	if len(entries) != 1 {
		t.Fatalf("got %d entries, want 1", len(entries))
	}
	if entries[0].device != "/dev/sdb1" {
		t.Errorf("entry device = %q, want /dev/sdb1", entries[0].device)
	}
}

func TestParseMountsWithTempFile(t *testing.T) {
	// Test that parseMounts can work with a file at a custom path.
	// NOTE: parseMounts() reads from /proc/mounts directly, so this test
	// creates a temp file and uses the parseMountsFromString helper.
	// This test demonstrates the fixture-file pattern for integration-style tests.
	dir := t.TempDir()
	mountsPath := filepath.Join(dir, "mounts")

	content := `/dev/nvme0n1p2 / ext4 rw,relatime 0 0
/dev/nvme0n1p1 /boot/efi vfat rw,relatime,fmask=0077,dmask=0077 0 0
tmpfs /tmp tmpfs rw,nosuid,nodev 0 0
`
	if err := os.WriteFile(mountsPath, []byte(content), 0644); err != nil {
		t.Fatal(err)
	}

	// Read and parse the temp file.
	data, err := os.ReadFile(mountsPath)
	if err != nil {
		t.Fatal(err)
	}

	entries := parseMountsData(string(data))

	if len(entries) != 2 {
		t.Fatalf("got %d entries, want 2 (ext4 and vfat, excluding tmpfs)", len(entries))
	}
}

func TestDiskInfoPayloadJSON(t *testing.T) {
	payload := diskInfoPayload{
		Disks: []diskInfoEntry{
			{
				Device:         "/dev/sda1",
				MountPoint:     "/",
				FSType:         "ext4",
				TotalBytes:     107374182400,
				UsedBytes:      53687091200,
				AvailableBytes: 48318382080,
				PercentUsed:    50.0,
			},
			{
				Device:         "/dev/sdb1",
				MountPoint:     "/data",
				FSType:         "xfs",
				TotalBytes:     1099511627776,
				UsedBytes:      549755813888,
				AvailableBytes: 549755813888,
				PercentUsed:    50.0,
			},
		},
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded diskInfoPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if len(decoded.Disks) != 2 {
		t.Fatalf("decoded %d disks, want 2", len(decoded.Disks))
	}
	if decoded.Disks[0].Device != "/dev/sda1" {
		t.Errorf("Disks[0].Device = %q, want /dev/sda1", decoded.Disks[0].Device)
	}
	if decoded.Disks[1].MountPoint != "/data" {
		t.Errorf("Disks[1].MountPoint = %q, want /data", decoded.Disks[1].MountPoint)
	}
}

func TestDiskPercentUsed(t *testing.T) {
	tests := []struct {
		name       string
		totalBytes int64
		usedBytes  int64
		wantPct    float64
	}{
		{"50% used", 100000, 50000, 50.0},
		{"0% used", 100000, 0, 0.0},
		{"100% used", 100000, 100000, 100.0},
		{"empty disk", 0, 0, 0.0},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := diskPercentUsed(tt.totalBytes, tt.usedBytes)
			if got != tt.wantPct {
				t.Errorf("diskPercentUsed(%d, %d) = %f, want %f", tt.totalBytes, tt.usedBytes, got, tt.wantPct)
			}
		})
	}
}
