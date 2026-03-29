// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"strings"
)

type diskInfoEntry struct {
	Device         string  `json:"device"`
	MountPoint     string  `json:"mount_point"`
	FSType         string  `json:"fs_type"`
	TotalBytes     int64   `json:"total_bytes"`
	UsedBytes      int64   `json:"used_bytes"`
	AvailableBytes int64   `json:"available_bytes"`
	PercentUsed    float64 `json:"percent_used"`
}

type diskInfoPayload struct {
	Disks []diskInfoEntry `json:"disks"`
}

// diskPercentUsed computes the disk usage percentage from total and used byte counts.
// Returns 0 if totalBytes is zero.
func diskPercentUsed(totalBytes, usedBytes int64) float64 {
	if totalBytes <= 0 {
		return 0.0
	}

	return float64(usedBytes) / float64(totalBytes) * 100.0
}

// pseudoFS lists filesystem types to exclude.
var pseudoFS = map[string]bool{
	"proc": true, "sysfs": true, "tmpfs": true, "devtmpfs": true,
	"devpts": true, "cgroup": true, "cgroup2": true, "pstore": true,
	"securityfs": true, "debugfs": true, "configfs": true, "fusectl": true,
	"mqueue": true, "hugetlbfs": true, "autofs": true, "binfmt_misc": true,
	"tracefs": true, "efivarfs": true, "bpf": true, "nsfs": true,
	"overlay": true, "squashfs": true, "ramfs": true, "rpc_pipefs": true,
	"nfsd": true, "sunrpc": true, "fuse.gvfsd-fuse": true,
}

type mountEntry struct {
	device     string
	mountPoint string
	fsType     string
}

// parseMountsData parses /proc/mounts content and returns real filesystem mount entries,
// deduplicating by mount point.
func parseMountsData(data string) []mountEntry {
	var entries []mountEntry
	seen := make(map[string]bool)

	for _, line := range strings.Split(data, "\n") {
		fields := strings.Fields(line)
		if len(fields) < 3 {
			continue
		}

		device := fields[0]
		mountPoint := fields[1]
		fsType := fields[2]

		if pseudoFS[fsType] {
			continue
		}

		// Deduplicate by mount point.
		if seen[mountPoint] {
			continue
		}
		seen[mountPoint] = true

		entries = append(entries, mountEntry{
			device:     device,
			mountPoint: mountPoint,
			fsType:     fsType,
		})
	}

	return entries
}
