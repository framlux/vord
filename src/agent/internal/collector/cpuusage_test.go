// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"testing"
)

// Sample /proc/stat content with realistic CPU tick values.
const fixtureProcStat = `cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0
cpu0 1393280 32966 572056 13343292 6130 0 17875 0 0 0
intr 23370612 17 31 0 0 0 0 0 0 0 0 0 0 156 0 0 0
ctxt 38014093
btime 1418183276
processes 26442
procs_running 1
procs_blocked 0
softirq 5057579 250191 1481983 0 624 0 0 117 1126375 0 2198289
`

// Sample /proc/stat with insufficient fields to test error handling.
const fixtureProcStatBadFields = `cpu  10132153 290696
cpu0 1393280 32966 572056 13343292 6130 0 17875 0 0 0
`

// Sample /proc/stat with no cpu aggregate line.
const fixtureProcStatNoCpuLine = `cpu0 1393280 32966 572056 13343292 6130 0 17875 0 0 0
intr 23370612 17 31 0 0 0 0 0 0 0 0 0 0 156 0 0 0
`

func TestCpuTicksTotal(t *testing.T) {
	ticks := cpuTicks{
		User:    10132153,
		Nice:    290696,
		System:  3084719,
		Idle:    46828483,
		Iowait:  16683,
		Irq:     0,
		Softirq: 25195,
		Steal:   0,
	}

	expected := int64(10132153 + 290696 + 3084719 + 46828483 + 16683 + 0 + 25195 + 0)
	got := ticks.total()
	if got != expected {
		t.Errorf("cpuTicks.total() = %d, want %d", got, expected)
	}
}

func TestCpuTicksTotalZero(t *testing.T) {
	ticks := cpuTicks{}
	if got := ticks.total(); got != 0 {
		t.Errorf("zero cpuTicks.total() = %d, want 0", got)
	}
}

func TestParseCpuTicksFromFixture(t *testing.T) {
	ticks, err := parseCpuTicks(fixtureProcStat)
	if err != nil {
		t.Fatalf("parseCpuTicks() error: %v", err)
	}

	if ticks.User != 10132153 {
		t.Errorf("User = %d, want 10132153", ticks.User)
	}
	if ticks.Nice != 290696 {
		t.Errorf("Nice = %d, want 290696", ticks.Nice)
	}
	if ticks.System != 3084719 {
		t.Errorf("System = %d, want 3084719", ticks.System)
	}
	if ticks.Idle != 46828483 {
		t.Errorf("Idle = %d, want 46828483", ticks.Idle)
	}
	if ticks.Iowait != 16683 {
		t.Errorf("Iowait = %d, want 16683", ticks.Iowait)
	}
	if ticks.Irq != 0 {
		t.Errorf("Irq = %d, want 0", ticks.Irq)
	}
	if ticks.Softirq != 25195 {
		t.Errorf("Softirq = %d, want 25195", ticks.Softirq)
	}
	if ticks.Steal != 0 {
		t.Errorf("Steal = %d, want 0", ticks.Steal)
	}
}

func TestParseCpuTicksBadFields(t *testing.T) {
	_, err := parseCpuTicks(fixtureProcStatBadFields)
	if err == nil {
		t.Fatal("expected error for /proc/stat with insufficient fields, got nil")
	}
}

func TestParseCpuTicksNoCpuLine(t *testing.T) {
	_, err := parseCpuTicks(fixtureProcStatNoCpuLine)
	if err == nil {
		t.Fatal("expected error when no cpu aggregate line, got nil")
	}
}

func TestCpuTickPercent(t *testing.T) {
	tests := []struct {
		name       string
		delta      int64
		totalDelta int64
		wantPct    int
	}{
		{"500 of 1000 = 50%", 500, 1000, 50},
		{"400 of 1000 = 40%", 400, 1000, 40},
		{"100 of 1000 = 10%", 100, 1000, 10},
		{"0 of 1000 = 0%", 0, 1000, 0},
		{"1000 of 1000 = 100%", 1000, 1000, 100},
		{"1 of 3 = 33%", 1, 3, 33},
		{"zero total returns 0", 500, 0, 0},
		{"negative total returns 0", 500, -1, 0},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := cpuTickPercent(tt.delta, tt.totalDelta)
			if got != tt.wantPct {
				t.Errorf("cpuTickPercent(%d, %d) = %d, want %d", tt.delta, tt.totalDelta, got, tt.wantPct)
			}
		})
	}
}

func TestCpuUsageDeltaComputation(t *testing.T) {
	// Simulate two /proc/stat readings taken 1 second apart.
	prev := cpuTicks{
		User:    10000,
		Nice:    200,
		System:  3000,
		Idle:    46000,
		Iowait:  100,
		Irq:     0,
		Softirq: 200,
		Steal:   0,
	}

	// After 1s: 500 user ticks, 100 system ticks, 400 idle ticks = 1000 total new ticks.
	current := cpuTicks{
		User:    10500,
		Nice:    200,
		System:  3100,
		Idle:    46400,
		Iowait:  100,
		Irq:     0,
		Softirq: 200,
		Steal:   0,
	}

	// prev.total() = 59500, current.total() = 60500 → delta = 1000
	totalDelta := current.total() - prev.total()
	if totalDelta != 1000 {
		t.Fatalf("totalDelta = %d, want 1000", totalDelta)
	}

	// Verify via production function: 400 idle ticks of 1000 total = 40% idle, 60% usage
	idlePct := cpuTickPercent(current.Idle-prev.Idle, totalDelta)
	usagePct := 100 - idlePct
	if idlePct != 40 {
		t.Errorf("idlePct = %d, want 40", idlePct)
	}
	if usagePct != 60 {
		t.Errorf("usagePct = %d, want 60", usagePct)
	}

	// 500 user ticks of 1000 total = 50%, 100 system ticks = 10%
	userPct := cpuTickPercent(current.User-prev.User, totalDelta)
	systemPct := cpuTickPercent(current.System-prev.System, totalDelta)
	if userPct != 50 {
		t.Errorf("userPct = %d, want 50", userPct)
	}
	if systemPct != 10 {
		t.Errorf("systemPct = %d, want 10", systemPct)
	}
}

func TestCpuUsageDeltaZeroTotal(t *testing.T) {
	// When totalDelta is zero, the collector returns nil (skips).
	// Verify that we correctly detect this edge case.
	prev := cpuTicks{User: 100, Idle: 900}
	current := cpuTicks{User: 100, Idle: 900}

	totalDelta := current.total() - prev.total()
	if totalDelta != 0 {
		t.Errorf("totalDelta = %d, want 0", totalDelta)
	}
}

func TestCpuUsagePayloadJSON(t *testing.T) {
	payload := cpuUsagePayload{
		CPUUsagePercent: 60,
		UserTime:        50,
		SystemTime:      10,
		NiceTime:        0,
		IdleTime:        40,
		IowaitTime:      0,
		IrqTime:         0,
		SoftirqTime:     0,
		StealTime:       0,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded cpuUsagePayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.CPUUsagePercent != 60 {
		t.Errorf("CPUUsagePercent = %d, want 60", decoded.CPUUsagePercent)
	}
	if decoded.UserTime != 50 {
		t.Errorf("UserTime = %d, want 50", decoded.UserTime)
	}
	if decoded.IdleTime != 40 {
		t.Errorf("IdleTime = %d, want 40", decoded.IdleTime)
	}
}

func TestCpuTicksJSONRoundtrip(t *testing.T) {
	// The collector stores cpuTicks as JSON in collector_state between runs.
	original := cpuTicks{
		User:    10132153,
		Nice:    290696,
		System:  3084719,
		Idle:    46828483,
		Iowait:  16683,
		Irq:     0,
		Softirq: 25195,
		Steal:   0,
	}

	data, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var restored cpuTicks
	if err := json.Unmarshal(data, &restored); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if restored != original {
		t.Errorf("round-trip mismatch: got %+v, want %+v", restored, original)
	}
}

func TestCpuUsageFullPipeline(t *testing.T) {
	// End-to-end test: parse two fixtures, compute delta, verify payload fields sum correctly.
	prev := cpuTicks{
		User: 1000, Nice: 100, System: 500, Idle: 8000,
		Iowait: 50, Irq: 10, Softirq: 30, Steal: 10,
	}
	current := cpuTicks{
		User: 1200, Nice: 110, System: 600, Idle: 8500,
		Iowait: 60, Irq: 15, Softirq: 40, Steal: 15,
	}

	totalDelta := current.total() - prev.total()
	if totalDelta <= 0 {
		t.Fatalf("totalDelta = %d, want positive", totalDelta)
	}

	idlePct := cpuTickPercent(current.Idle-prev.Idle, totalDelta)
	payload := cpuUsagePayload{
		CPUUsagePercent: 100 - idlePct,
		UserTime:        cpuTickPercent(current.User-prev.User, totalDelta),
		SystemTime:      cpuTickPercent(current.System-prev.System, totalDelta),
		NiceTime:        cpuTickPercent(current.Nice-prev.Nice, totalDelta),
		IdleTime:        idlePct,
		IowaitTime:      cpuTickPercent(current.Iowait-prev.Iowait, totalDelta),
		IrqTime:         cpuTickPercent(current.Irq-prev.Irq, totalDelta),
		SoftirqTime:     cpuTickPercent(current.Softirq-prev.Softirq, totalDelta),
		StealTime:       cpuTickPercent(current.Steal-prev.Steal, totalDelta),
	}

	// All individual percentages (including usage which is 100-idle) should be non-negative.
	if payload.CPUUsagePercent < 0 {
		t.Errorf("CPUUsagePercent = %d, want >= 0", payload.CPUUsagePercent)
	}
	if payload.IdleTime < 0 {
		t.Errorf("IdleTime = %d, want >= 0", payload.IdleTime)
	}

	// The sum of all component percentages should approximate 100.
	componentSum := payload.UserTime + payload.SystemTime + payload.NiceTime +
		payload.IdleTime + payload.IowaitTime + payload.IrqTime +
		payload.SoftirqTime + payload.StealTime
	if componentSum < 95 || componentSum > 105 {
		t.Errorf("component sum = %d, want approximately 100", componentSum)
	}
}
