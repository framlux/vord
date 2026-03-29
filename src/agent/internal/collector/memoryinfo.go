// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"fmt"
	"strings"
)

type memoryInfoPayload struct {
	MemoryTotal     int64 `json:"memory_total"`
	MemoryFree      int64 `json:"memory_free"`
	MemoryAvailable int64 `json:"memory_available"`
	SwapTotal       int64 `json:"swap_total"`
	SwapFree        int64 `json:"swap_free"`
}

// parseMeminfoData parses /proc/meminfo content and returns values in bytes.
func parseMeminfoData(data string) map[string]int64 {
	result := make(map[string]int64)

	for _, line := range strings.Split(data, "\n") {
		parts := strings.SplitN(line, ":", 2)
		if len(parts) != 2 {
			continue
		}
		key := strings.TrimSpace(parts[0])
		valStr := strings.TrimSpace(parts[1])

		var val int64
		if strings.HasSuffix(valStr, " kB") {
			_, err := fmt.Sscanf(valStr, "%d kB", &val)
			if err == nil {
				result[key] = val * 1024
			}
		}
	}

	return result
}
