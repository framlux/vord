// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

type memUsagePayload struct {
	MemoryTotal        int64 `json:"memory_total"`
	MemoryFree         int64 `json:"memory_free"`
	MemoryUsed         int64 `json:"memory_used"`
	MemoryUsagePercent int   `json:"memory_usage_percent"`
}

// memUsagePercent computes memory usage as a percentage of total.
// Returns 0 if total is zero.
func memUsagePercent(total, available int64) int {
	if total <= 0 {
		return 0
	}
	used := total - available

	return int(float64(used) / float64(total) * 100)
}
