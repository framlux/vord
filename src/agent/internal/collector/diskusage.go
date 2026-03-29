// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

type diskUsageEntry struct {
	Device          string `json:"device"`
	Path            string `json:"path"`
	BlocksSize      int64  `json:"blocks_size"`
	Blocks          int64  `json:"blocks"`
	BlocksFree      int64  `json:"blocks_free"`
	BlocksAvailable int64  `json:"blocks_available"`
	BlocksUsed      int64  `json:"blocks_used"`
	UsagePercent    int    `json:"usage_percent"`
}

type diskUsagePayload struct {
	Disks []diskUsageEntry `json:"disks"`
}

// diskUsagePercent computes the usage percentage given total and free block counts.
// Returns 0 if blocks is zero.
func diskUsagePercent(blocks, blocksFree int64) int {
	if blocks <= 0 {
		return 0
	}
	blocksUsed := blocks - blocksFree

	return int(float64(blocksUsed) / float64(blocks) * 100)
}
