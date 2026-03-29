// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"fmt"
	"strconv"
	"strings"
)

type cpuUsagePayload struct {
	CPUUsagePercent int `json:"cpu_usage_percent"`
	UserTime        int `json:"user_time"`
	SystemTime      int `json:"system_time"`
	NiceTime        int `json:"nice_time"`
	IdleTime        int `json:"idle_time"`
	IowaitTime      int `json:"iowait_time"`
	IrqTime         int `json:"irq_time"`
	SoftirqTime     int `json:"softirq_time"`
	StealTime       int `json:"steal_time"`
}

type cpuTicks struct {
	User    int64 `json:"user"`
	Nice    int64 `json:"nice"`
	System  int64 `json:"system"`
	Idle    int64 `json:"idle"`
	Iowait  int64 `json:"iowait"`
	Irq     int64 `json:"irq"`
	Softirq int64 `json:"softirq"`
	Steal   int64 `json:"steal"`
}

func (t cpuTicks) total() int64 {
	return t.User + t.Nice + t.System + t.Idle + t.Iowait + t.Irq + t.Softirq + t.Steal
}

// cpuTickPercent computes the percentage a tick delta represents of the total delta.
// Returns 0 if totalDelta is zero.
func cpuTickPercent(delta, totalDelta int64) int {
	if totalDelta <= 0 {
		return 0
	}

	pct := int(float64(delta) / float64(totalDelta) * 100)
	if pct < 0 {
		return 0
	}
	if pct > 100 {
		return 100
	}

	return pct
}

// parseCpuTicks parses /proc/stat content and returns the aggregate CPU tick values.
func parseCpuTicks(data string) (cpuTicks, error) {
	for _, line := range strings.Split(data, "\n") {
		if strings.HasPrefix(line, "cpu ") == false {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 9 {
			return cpuTicks{}, fmt.Errorf("unexpected /proc/stat format: %d fields", len(fields))
		}

		parse := func(s string) (int64, error) {
			v, err := strconv.ParseInt(s, 10, 64)
			if err != nil {
				return 0, fmt.Errorf("parsing tick value %q: %w", s, err)
			}

			return v, nil
		}

		var ticks cpuTicks
		var parseErr error
		if ticks.User, parseErr = parse(fields[1]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Nice, parseErr = parse(fields[2]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.System, parseErr = parse(fields[3]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Idle, parseErr = parse(fields[4]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Iowait, parseErr = parse(fields[5]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Irq, parseErr = parse(fields[6]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Softirq, parseErr = parse(fields[7]); parseErr != nil {
			return cpuTicks{}, parseErr
		}
		if ticks.Steal, parseErr = parse(fields[8]); parseErr != nil {
			return cpuTicks{}, parseErr
		}

		return ticks, nil
	}

	return cpuTicks{}, fmt.Errorf("cpu line not found in /proc/stat")
}
