// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"strings"
)

type cpuInfoPayload struct {
	DeviceID          string `json:"device_id"`
	Model             string `json:"model"`
	Manufacturer      string `json:"manufacturer"`
	ProcessorType     string `json:"processor_type"`
	CPUStatus         int    `json:"cpu_status"`
	NumberOfCores     string `json:"number_of_cores"`
	LogicalProcessors int    `json:"logical_processors"`
	AddressWidth      string `json:"address_width"`
	CurrentClockSpeed int    `json:"current_clock_speed"`
	MaxClockSpeed     int    `json:"max_clock_speed"`
	SocketDesignation string `json:"socket_designation"`
}

// parseProcCpuinfoData parses /proc/cpuinfo content and returns key-value pairs
// from the first processor block.
func parseProcCpuinfoData(data string) map[string]string {
	result := make(map[string]string)

	for _, line := range strings.Split(data, "\n") {
		if line == "" {
			// Only capture first processor block, but don't break so we can get all fields.
			if _, ok := result["model name"]; ok {
				break
			}
			continue
		}
		parts := strings.SplitN(line, ":", 2)
		if len(parts) == 2 {
			key := strings.TrimSpace(parts[0])
			value := strings.TrimSpace(parts[1])
			result[key] = value
		}
	}

	return result
}
