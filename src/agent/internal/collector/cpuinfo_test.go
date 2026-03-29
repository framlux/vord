// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"strconv"
	"strings"
	"testing"
)

// Fixture /proc/cpuinfo for a dual-socket Xeon system with 2 cores per socket
// and hyperthreading (4 logical CPUs total).
const fixtureProcCpuinfo = `processor	: 0
vendor_id	: GenuineIntel
cpu family	: 6
model		: 85
model name	: Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz
stepping	: 7
microcode	: 0x5003604
cpu MHz		: 2494.140
cache size	: 28160 KB
physical id	: 0
siblings	: 2
core id		: 0
cpu cores	: 2
apicid		: 0
initial apicid	: 0
fpu		: yes
fpu_exception	: yes
cpuid level	: 22
wp		: yes
flags		: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht
bogomips	: 4988.28
clflush size	: 64
cache_alignment	: 64
address sizes	: 46 bits physical, 48 bits virtual
power management:

processor	: 1
vendor_id	: GenuineIntel
cpu family	: 6
model		: 85
model name	: Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz
stepping	: 7
microcode	: 0x5003604
cpu MHz		: 2494.140
cache size	: 28160 KB
physical id	: 0
siblings	: 2
core id		: 1
cpu cores	: 2
apicid		: 1
initial apicid	: 1
fpu		: yes
fpu_exception	: yes
cpuid level	: 22
wp		: yes
flags		: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht
bogomips	: 4988.28
clflush size	: 64
cache_alignment	: 64
address sizes	: 46 bits physical, 48 bits virtual
power management:

processor	: 2
vendor_id	: GenuineIntel
cpu family	: 6
model		: 85
model name	: Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz
stepping	: 7
microcode	: 0x5003604
cpu MHz		: 2494.140
cache size	: 28160 KB
physical id	: 1
siblings	: 2
core id		: 0
cpu cores	: 2
apicid		: 2
initial apicid	: 2
fpu		: yes
fpu_exception	: yes
cpuid level	: 22
wp		: yes
flags		: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht
bogomips	: 4988.28
clflush size	: 64
cache_alignment	: 64
address sizes	: 46 bits physical, 48 bits virtual
power management:

processor	: 3
vendor_id	: GenuineIntel
cpu family	: 6
model		: 85
model name	: Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz
stepping	: 7
microcode	: 0x5003604
cpu MHz		: 2494.140
cache size	: 28160 KB
physical id	: 1
siblings	: 2
core id		: 1
cpu cores	: 2
apicid		: 3
initial apicid	: 3
fpu		: yes
fpu_exception	: yes
cpuid level	: 22
wp		: yes
flags		: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ss ht
bogomips	: 4988.28
clflush size	: 64
cache_alignment	: 64
address sizes	: 46 bits physical, 48 bits virtual
power management:

`

// Fixture /proc/cpuinfo for a single-core ARM processor.
const fixtureProcCpuinfoARM = `processor	: 0
BogoMIPS	: 48.00
Features	: fp asimd evtstrm aes pmull sha1 sha2 crc32 atomics
CPU implementer	: 0x41
CPU architecture: 8
CPU variant	: 0x1
CPU part	: 0xd07
CPU revision	: 4

`

func TestParseProcCpuinfo(t *testing.T) {
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfo)

	tests := []struct {
		key  string
		want string
	}{
		{"model name", "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz"},
		{"vendor_id", "GenuineIntel"},
		{"cpu MHz", "2494.140"},
		{"physical id", "0"},
		{"cpu cores", "2"},
		{"cache size", "28160 KB"},
		{"processor", "0"},
	}

	for _, tt := range tests {
		t.Run(tt.key, func(t *testing.T) {
			got := cpuinfo[tt.key]
			if got != tt.want {
				t.Errorf("cpuinfo[%q] = %q, want %q", tt.key, got, tt.want)
			}
		})
	}
}

func TestParseProcCpuinfoFirstBlockOnly(t *testing.T) {
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfo)

	// The parser should only capture the first processor block.
	// Processor 0 has physical id "0".
	if cpuinfo["physical id"] != "0" {
		t.Errorf("physical id = %q, want '0' (from first block)", cpuinfo["physical id"])
	}
	// It should NOT have data from processor 2/3 (physical id "1").
}

func TestParseProcCpuinfoEmpty(t *testing.T) {
	cpuinfo := parseProcCpuinfoData("")
	if len(cpuinfo) != 0 {
		t.Errorf("empty input should produce empty map, got %d entries", len(cpuinfo))
	}
}

func TestParseProcCpuinfoARM(t *testing.T) {
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfoARM)

	// ARM cpuinfo does not have "model name" field.
	if _, ok := cpuinfo["model name"]; ok {
		t.Error("ARM cpuinfo should not have 'model name' field")
	}

	// It should have ARM-specific fields.
	if cpuinfo["CPU architecture"] != "8" {
		t.Errorf("CPU architecture = %q, want '8'", cpuinfo["CPU architecture"])
	}
}

func TestCpuBrandFromProc(t *testing.T) {
	brand := cpuBrandFromData(fixtureProcCpuinfo)
	if brand != "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz" {
		t.Errorf("cpuBrand = %q, want 'Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz'", brand)
	}
}

func TestCpuBrandFromProcARM(t *testing.T) {
	brand := cpuBrandFromData(fixtureProcCpuinfoARM)
	if brand != "" {
		t.Errorf("ARM cpuBrand = %q, want empty string", brand)
	}
}

func TestCpuBrandFromProcEmpty(t *testing.T) {
	brand := cpuBrandFromData("")
	if brand != "" {
		t.Errorf("empty cpuBrand = %q, want empty string", brand)
	}
}

func TestCountPhysicalCores(t *testing.T) {
	// The fixture has 2 sockets (physical id 0, 1) with 2 cores each (core id 0, 1).
	// That is 4 unique physical:core combinations.
	count := countPhysicalCoresFromData(fixtureProcCpuinfo)
	if count != 4 {
		t.Errorf("countPhysicalCores = %d, want 4", count)
	}
}

func TestCountPhysicalCoresARM(t *testing.T) {
	// ARM cpuinfo does not have physical id/core id fields.
	count := countPhysicalCoresFromData(fixtureProcCpuinfoARM)
	if count != 0 {
		t.Errorf("ARM countPhysicalCores = %d, want 0 (no physical id/core id)", count)
	}
}

func TestCountPhysicalCoresEmpty(t *testing.T) {
	count := countPhysicalCoresFromData("")
	if count != 0 {
		t.Errorf("empty countPhysicalCores = %d, want 0", count)
	}
}

func TestCpuMHzParsing(t *testing.T) {
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfo)

	mhzStr := cpuinfo["cpu MHz"]
	currentMhz, _ := strconv.Atoi(strings.Split(mhzStr, ".")[0])

	if currentMhz != 2494 {
		t.Errorf("currentMhz = %d, want 2494", currentMhz)
	}
}

func TestCpuMHzParsingMissing(t *testing.T) {
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfoARM)

	mhzStr := cpuinfo["cpu MHz"]
	currentMhz, _ := strconv.Atoi(strings.Split(mhzStr, ".")[0])

	// Missing value should result in 0.
	if currentMhz != 0 {
		t.Errorf("ARM currentMhz = %d, want 0", currentMhz)
	}
}

func TestCpuInfoPayloadJSON(t *testing.T) {
	payload := cpuInfoPayload{
		DeviceID:          "CPU0",
		Model:             "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz",
		Manufacturer:      "GenuineIntel",
		ProcessorType:     "amd64",
		CPUStatus:         1,
		NumberOfCores:     "4",
		LogicalProcessors: 8,
		AddressWidth:      "64",
		CurrentClockSpeed: 2494,
		MaxClockSpeed:     2494,
		SocketDesignation: "0",
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded cpuInfoPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.Model != "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz" {
		t.Errorf("Model = %q, want expected value", decoded.Model)
	}
	if decoded.Manufacturer != "GenuineIntel" {
		t.Errorf("Manufacturer = %q, want 'GenuineIntel'", decoded.Manufacturer)
	}
	if decoded.CPUStatus != 1 {
		t.Errorf("CPUStatus = %d, want 1", decoded.CPUStatus)
	}
	if decoded.NumberOfCores != "4" {
		t.Errorf("NumberOfCores = %q, want '4'", decoded.NumberOfCores)
	}
	if decoded.CurrentClockSpeed != 2494 {
		t.Errorf("CurrentClockSpeed = %d, want 2494", decoded.CurrentClockSpeed)
	}
}

func TestCpuInfoFullPipeline(t *testing.T) {
	// End-to-end test: parse cpuinfo fixture, build payload.
	cpuinfo := parseProcCpuinfoData(fixtureProcCpuinfo)

	currentMhz, _ := strconv.Atoi(strings.Split(cpuinfo["cpu MHz"], ".")[0])
	physicalCores := countPhysicalCoresFromData(fixtureProcCpuinfo)

	payload := cpuInfoPayload{
		DeviceID:          "CPU0",
		Model:             cpuinfo["model name"],
		Manufacturer:      cpuinfo["vendor_id"],
		ProcessorType:     "amd64",
		CPUStatus:         1,
		NumberOfCores:     strconv.Itoa(physicalCores),
		LogicalProcessors: 4, // from fixture: 4 processor entries
		AddressWidth:      "64",
		CurrentClockSpeed: currentMhz,
		MaxClockSpeed:     currentMhz,
		SocketDesignation: cpuinfo["physical id"],
	}

	if payload.Model == "" {
		t.Error("Model should not be empty")
	}
	if payload.Manufacturer == "" {
		t.Error("Manufacturer should not be empty")
	}
	if payload.NumberOfCores != "4" {
		t.Errorf("NumberOfCores = %q, want '4'", payload.NumberOfCores)
	}
	if payload.CurrentClockSpeed <= 0 {
		t.Errorf("CurrentClockSpeed = %d, want positive", payload.CurrentClockSpeed)
	}
}
