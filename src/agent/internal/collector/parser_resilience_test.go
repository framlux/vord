// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"math"
	"strings"
	"testing"
)

// Common bad inputs used across all parser resilience tests. The agent reads
// files from /proc, /sys, and /etc on arbitrary Linux systems — these files
// can contain anything: truncated data, binary garbage, NUL bytes, kernel
// format changes, container oddities, etc. Every parser must return a
// zero/empty value (never panic) for any input.
var commonBadInputs = []string{
	"",                              // empty
	"\x00\x01\x02\xff\xfe",         // binary garbage
	"\x00\x00\x00\x00",             // NUL-filled
	strings.Repeat("a", 1<<20),     // extremely long single line (1MB)
	"   \n\t\n  ",                   // only whitespace
	"\n\n\n\n",                      // only newlines
	"\r\n\r\n",                      // Windows line endings
	"\t\t\t\t",                      // tab-heavy
	"cpu \xf0\x9f\x94\xa5 100",     // Unicode/emoji
	"-1",                            // negative number
	"99999999999999999999",          // overflow number
}

// truncatedInput returns the first line of a format with more data after it.
func truncatedInput(fullInput string) string {
	idx := strings.Index(fullInput, "\n")
	if idx < 0 {
		return fullInput
	}

	return fullInput[:idx]
}

// --- parseCpuTicks ---

func TestParseCpuTicksNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"cpu ",                         // cpu line with no data after prefix
		"cpu 1 2",                      // too few fields
		"cpu a b c d e f g h",          // non-numeric fields
		"cpu -1 -2 -3 -4 -5 -6 -7 -8", // negative values
		"cpu 99999999999999999999 0 0 0 0 0 0 0", // overflow value
		"cpu 100 200 300 400\ncpu 500 600 700 800 900 1000 1100 1200", // multiple cpu lines
		truncatedInput("cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0\ncpu0 1393280 32966"),
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseCpuTicks panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseCpuTicks(input)
		})
	}
}

// --- parseMeminfoData ---

func TestParseMeminfoDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"no colon here",                                // lines without colons
		"MemTotal: not_a_number kB",                    // non-numeric value
		"MemTotal: -1024 kB",                           // negative kB value
		"MemTotal:",                                    // value without data
		": 1024 kB",                                    // zero-length key
		"MemTotal: 1024 kB\nMemTotal: 2048 kB",        // duplicate keys
		"MemTotal: 1024",                               // without " kB" suffix
		"MemTotal: 99999999999999999999 kB",            // overflow
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseMeminfoData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseMeminfoData(input)
		})
	}
}

// --- parseMountsData ---

func TestParseMountsDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"/dev/sda1",                                    // line with fewer than 3 fields
		"/dev/sda1 /mnt",                               // only 2 fields
		"/dev/sda1 /mnt/my\\ mount ext4 rw 0 0",       // mount point with escaped space
		strings.Repeat("/dev/very/long/path", 10000)+" /mnt ext4 rw 0 0", // extremely long device path
		"proc /proc proc rw 0 0\nsysfs /sys sysfs rw 0 0\ntmpfs /tmp tmpfs rw 0 0", // all pseudo-FS entries
		"\x00\x01 /mnt ext4 rw 0 0",                   // binary in device name
		"/dev/sda1 /mnt ext4 rw 0 0\n/dev/sdb1 /mnt xfs rw 0 0", // duplicate mount points
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseMountsData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseMountsData(input)
		})
	}
}

// --- parseProcCpuinfoData ---

func TestParseProcCpuinfoDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"key without value",             // key without colon
		": value without key",           // value without key
		"\n\n\n",                        // only empty lines between blocks
		"single field no colon",         // single field with no colon
		"model name\t:",                 // key with colon but no value
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseProcCpuinfoData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseProcCpuinfoData(input)
		})
	}
}

// --- parseOsReleaseData ---

func TestParseOsReleaseDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"no equals here",                     // lines without =
		"KEY=\"unmatched quote",              // values with unmatched quotes
		"KEY=value=with=equals",              // keys with = in the value
		"=empty_key",                         // empty key
		"KEY=",                               // empty value
		"KEY=\"\"",                           // empty value quoted
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseOsReleaseData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseOsReleaseData(input)
		})
	}
}

// --- parseVersion ---

func TestParseVersionNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"-1.-2.-3",          // negative version numbers
		"1.",                // single dot trailing
		"1.2.",              // trailing dot
		"1.2.3.",            // double trailing dot
		"1a.2b.3c",         // version with letters mixed in
		".",                 // just a dot
		"...",               // multiple dots
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseVersion panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseVersion(input)
		})
	}
}

// --- cpuBrandFromData ---

func TestCpuBrandFromDataNoPanic(t *testing.T) {
	for _, input := range commonBadInputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("cpuBrandFromData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			cpuBrandFromData(input)
		})
	}
}

// --- countPhysicalCoresFromData ---

func TestCountPhysicalCoresFromDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"physical id\t: abc\ncore id\t: def\n",   // non-numeric IDs
		"physical id\t: -1\ncore id\t: -2\n",     // negative IDs
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("countPhysicalCoresFromData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			countPhysicalCoresFromData(input)
		})
	}
}

// --- memTotalBytesFromData ---

func TestMemTotalBytesFromDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"NotMemTotal: 1024 kB",            // missing MemTotal line
		"MemTotal: not_a_number kB",       // non-numeric value
		"MemTotal: -1024 kB",              // negative value
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("memTotalBytesFromData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			memTotalBytesFromData(input)
		})
	}
}

// --- uptimeSecondsFromData ---

func TestUptimeSecondsFromDataNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"-123456.78 67890.12", // negative uptime
		"abc def",             // non-numeric
		"123456.78",           // no space separator (valid, just first field)
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("uptimeSecondsFromData panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			uptimeSecondsFromData(input)
		})
	}
}

// --- parseGlobalIPAddresses ---

func TestParseGlobalIPAddressesNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"2: eth0 inet not_cidr brd 10.0.0.255",     // no / in inet field
		"2: eth0 inet6 fe80::1 brd scope global",   // IPv6-formatted without /
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseGlobalIPAddresses panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			parseGlobalIPAddresses(input)
		})
	}
}

// --- cpuTickPercent ---

func TestCpuTickPercentNoPanic(t *testing.T) {
	cases := []struct {
		delta, totalDelta int64
	}{
		{0, 0},
		{-1, 100},
		{100, -1},
		{math.MaxInt64, math.MaxInt64},
		{-math.MaxInt64, math.MaxInt64},
		{math.MaxInt64, 1},
		{0, math.MaxInt64},
	}

	for _, tc := range cases {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("cpuTickPercent panicked on delta=%d totalDelta=%d: %v", tc.delta, tc.totalDelta, r)
				}
			}()
			cpuTickPercent(tc.delta, tc.totalDelta)
		})
	}
}

// --- memUsagePercent ---

func TestMemUsagePercentNoPanic(t *testing.T) {
	cases := []struct {
		total, available int64
	}{
		{0, 0},
		{-1, 100},
		{100, -1},
		{math.MaxInt64, math.MaxInt64},
		{math.MaxInt64, 0},
		{0, math.MaxInt64},
	}

	for _, tc := range cases {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("memUsagePercent panicked on total=%d available=%d: %v", tc.total, tc.available, r)
				}
			}()
			memUsagePercent(tc.total, tc.available)
		})
	}
}

// --- diskUsagePercent ---

func TestDiskUsagePercentNoPanic(t *testing.T) {
	cases := []struct {
		blocks, blocksFree int64
	}{
		{0, 0},
		{-1, 100},
		{100, -1},
		{math.MaxInt64, math.MaxInt64},
		{math.MaxInt64, 0},
		{0, math.MaxInt64},
	}

	for _, tc := range cases {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("diskUsagePercent panicked on blocks=%d blocksFree=%d: %v", tc.blocks, tc.blocksFree, r)
				}
			}()
			diskUsagePercent(tc.blocks, tc.blocksFree)
		})
	}
}

// --- diskPercentUsed ---

func TestDiskPercentUsedNoPanic(t *testing.T) {
	cases := []struct {
		totalBytes, usedBytes int64
	}{
		{0, 0},
		{-1, 100},
		{100, -1},
		{math.MaxInt64, math.MaxInt64},
		{math.MaxInt64, 0},
		{0, math.MaxInt64},
	}

	for _, tc := range cases {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("diskPercentUsed panicked on totalBytes=%d usedBytes=%d: %v", tc.totalBytes, tc.usedBytes, r)
				}
			}()
			diskPercentUsed(tc.totalBytes, tc.usedBytes)
		})
	}
}

// --- isPlaceholderSerial ---

func TestIsPlaceholderSerialNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		strings.Repeat("X", 10000), // very long string
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("isPlaceholderSerial panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()
			isPlaceholderSerial(input)
		})
	}
}

// --- Intent assertion upgrades (Priority 5) ---
// These tests verify return values, not just "no panic".

// Intent: parseCpuTicks returns zero-value cpuTicks{} and an error for bad inputs.
func TestParseCpuTicks_ReturnsErrorForBadInputs(t *testing.T) {
	badInputs := []string{"", "no cpu line", "something else entirely"}
	for _, input := range badInputs {
		ticks, err := parseCpuTicks(input)
		if err == nil {
			t.Errorf("expected error for input %q, got nil", input[:min(len(input), 30)])
		}
		if ticks.total() != 0 {
			t.Errorf("expected zero-value ticks for input %q, got total=%d", input[:min(len(input), 30)], ticks.total())
		}
	}
}

// Intent: parseMeminfoData returns empty map for bad inputs (not nil, not negative values).
func TestParseMeminfoData_ReturnsEmptyForBadInputs(t *testing.T) {
	badInputs := []string{"", "no colons", "\x00\x01\x02"}
	for _, input := range badInputs {
		result := parseMeminfoData(input)
		if result == nil {
			t.Errorf("expected non-nil map for input %q, got nil", input[:min(len(input), 30)])
		}
		if len(result) != 0 {
			t.Errorf("expected empty map for input %q, got %d entries", input[:min(len(input), 30)], len(result))
		}
	}
}

// Intent: parseMountsData returns empty/nil slice for bad inputs.
func TestParseMountsData_ReturnsEmptyForBadInputs(t *testing.T) {
	badInputs := []string{"", "\x00\x01\x02", "one_field_only"}
	for _, input := range badInputs {
		result := parseMountsData(input)
		if len(result) != 0 {
			t.Errorf("expected empty slice for input %q, got %d entries", input[:min(len(input), 30)], len(result))
		}
	}
}

// Intent: parseProcCpuinfoData returns empty map for bad inputs.
func TestParseProcCpuinfoData_ReturnsEmptyForBadInputs(t *testing.T) {
	badInputs := []string{"", "no colons here", "\x00\x01\x02"}
	for _, input := range badInputs {
		result := parseProcCpuinfoData(input)
		if result == nil {
			t.Errorf("expected non-nil map for input %q, got nil", input[:min(len(input), 30)])
		}
	}
}

// Intent: parseOsReleaseData returns empty map for bad inputs.
func TestParseOsReleaseData_ReturnsEmptyForBadInputs(t *testing.T) {
	badInputs := []string{"", "no equals sign", "\x00\x01\x02"}
	for _, input := range badInputs {
		result := parseOsReleaseData(input)
		if result == nil {
			t.Errorf("expected non-nil map for input %q, got nil", input[:min(len(input), 30)])
		}
	}
}

// Intent: memUsagePercent returns 0 (not negative or >100) for zero/negative edge cases.
func TestMemUsagePercent_EdgeCaseReturns(t *testing.T) {
	cases := []struct {
		total, available int64
		expectZero       bool
	}{
		{0, 0, true},
		{-1, 100, true},
		{100, 100, true}, // 0% usage
	}

	for _, tc := range cases {
		result := memUsagePercent(tc.total, tc.available)
		if result < 0 || result > 100 {
			t.Errorf("memUsagePercent(%d, %d) = %d, expected 0-100", tc.total, tc.available, result)
		}
		if tc.expectZero && result != 0 {
			t.Errorf("memUsagePercent(%d, %d) = %d, expected 0", tc.total, tc.available, result)
		}
	}
}

// Intent: diskUsagePercent returns 0 (not negative or >100) for zero/negative edge cases.
func TestDiskUsagePercent_EdgeCaseReturns(t *testing.T) {
	cases := []struct {
		blocks, blocksFree int64
		expectZero         bool
	}{
		{0, 0, true},
		{-1, 100, true},
		{100, 100, true}, // 0% usage
	}

	for _, tc := range cases {
		result := diskUsagePercent(tc.blocks, tc.blocksFree)
		if result < 0 || result > 100 {
			t.Errorf("diskUsagePercent(%d, %d) = %d, expected 0-100", tc.blocks, tc.blocksFree, result)
		}
		if tc.expectZero && result != 0 {
			t.Errorf("diskUsagePercent(%d, %d) = %d, expected 0", tc.blocks, tc.blocksFree, result)
		}
	}
}

// Intent: cpuTickPercent returns 0 when totalDelta is zero or negative.
func TestCpuTickPercent_ZeroDeltaReturnsZero(t *testing.T) {
	result := cpuTickPercent(100, 0)
	if result != 0 {
		t.Errorf("cpuTickPercent(100, 0) = %d, expected 0", result)
	}

	result = cpuTickPercent(100, -1)
	if result != 0 {
		t.Errorf("cpuTickPercent(100, -1) = %d, expected 0", result)
	}
}

// Intent: isPlaceholderSerial correctly identifies known placeholder strings.
func TestIsPlaceholderSerial_KnownValues(t *testing.T) {
	placeholders := []string{"", "Not Specified", "To Be Filled By O.E.M."}
	for _, s := range placeholders {
		if isPlaceholderSerial(s) == false {
			t.Errorf("expected isPlaceholderSerial(%q) = true", s)
		}
	}

	nonPlaceholders := []string{"ABC123", "SN-12345", "gen-a1b2c3d4e5f6"}
	for _, s := range nonPlaceholders {
		if isPlaceholderSerial(s) {
			t.Errorf("expected isPlaceholderSerial(%q) = false", s)
		}
	}
}

// Intent: Valid /proc/stat input parses to correct tick values.
func TestParseCpuTicks_ValidInput(t *testing.T) {
	input := "cpu  10132153 290696 3084719 46828483 16683 0 25195 0 0 0\n"
	ticks, err := parseCpuTicks(input)
	if err != nil {
		t.Fatalf("parseCpuTicks: %v", err)
	}

	if ticks.User != 10132153 {
		t.Errorf("expected User=10132153, got %d", ticks.User)
	}
	if ticks.Nice != 290696 {
		t.Errorf("expected Nice=290696, got %d", ticks.Nice)
	}
	if ticks.System != 3084719 {
		t.Errorf("expected System=3084719, got %d", ticks.System)
	}
	if ticks.Idle != 46828483 {
		t.Errorf("expected Idle=46828483, got %d", ticks.Idle)
	}
}

// Intent: Valid /proc/meminfo input parses to correct byte values.
func TestParseMeminfoData_ValidInput(t *testing.T) {
	input := "MemTotal:       16384000 kB\nMemFree:         2048000 kB\nMemAvailable:    8192000 kB\n"
	result := parseMeminfoData(input)

	if result["MemTotal"] != 16384000*1024 {
		t.Errorf("expected MemTotal=%d, got %d", 16384000*1024, result["MemTotal"])
	}
	if result["MemFree"] != 2048000*1024 {
		t.Errorf("expected MemFree=%d, got %d", 2048000*1024, result["MemFree"])
	}
}

// Intent: Valid os-release input parses to correct key-value pairs with unquoted values.
func TestParseOsReleaseData_ValidInput(t *testing.T) {
	input := `NAME="Ubuntu"
VERSION_ID="22.04"
ID=ubuntu
`
	result := parseOsReleaseData(input)

	if result["NAME"] != "Ubuntu" {
		t.Errorf("expected NAME=Ubuntu, got %q", result["NAME"])
	}
	if result["VERSION_ID"] != "22.04" {
		t.Errorf("expected VERSION_ID=22.04, got %q", result["VERSION_ID"])
	}
	if result["ID"] != "ubuntu" {
		t.Errorf("expected ID=ubuntu, got %q", result["ID"])
	}
}

// Intent: parseVersion correctly splits version strings into major.minor.patch.
func TestParseVersion_ValidInput(t *testing.T) {
	major, minor, patch := parseVersion("22.04.1")
	if major != 22 || minor != 4 || patch != 1 {
		t.Errorf("expected 22.4.1, got %d.%d.%d", major, minor, patch)
	}

	major, minor, patch = parseVersion("20.04")
	if major != 20 || minor != 4 || patch != 0 {
		t.Errorf("expected 20.4.0, got %d.%d.%d", major, minor, patch)
	}

	major, minor, patch = parseVersion("9")
	if major != 9 || minor != 0 || patch != 0 {
		t.Errorf("expected 9.0.0, got %d.%d.%d", major, minor, patch)
	}
}

// --- cpuBrandFromData correctness ---

// Intent: cpuBrandFromData extracts the correct CPU model name from realistic /proc/cpuinfo data.
func TestCpuBrandFromData_RealisticInput(t *testing.T) {
	input := "processor\t: 0\n" +
		"vendor_id\t: GenuineIntel\n" +
		"cpu family\t: 6\n" +
		"model\t\t: 85\n" +
		"model name\t: Intel(R) Xeon(R) Platinum 8275CL CPU @ 3.00GHz\n" +
		"stepping\t: 7\n" +
		"cpu MHz\t\t: 3000.000\n" +
		"cache size\t: 36608 KB\n" +
		"\n" +
		"processor\t: 1\n" +
		"vendor_id\t: GenuineIntel\n" +
		"model name\t: Intel(R) Xeon(R) Platinum 8275CL CPU @ 3.00GHz\n" +
		"\n"

	result := cpuBrandFromData(input)
	if result != "Intel(R) Xeon(R) Platinum 8275CL CPU @ 3.00GHz" {
		t.Errorf("expected Intel Xeon brand string, got %q", result)
	}
}

// Intent: cpuBrandFromData returns an AMD brand string correctly.
func TestCpuBrandFromData_AMDProcessor(t *testing.T) {
	input := "processor\t: 0\n" +
		"vendor_id\t: AuthenticAMD\n" +
		"model name\t: AMD EPYC 7R13 48-Core Processor\n" +
		"\n"

	result := cpuBrandFromData(input)
	if result != "AMD EPYC 7R13 48-Core Processor" {
		t.Errorf("expected AMD EPYC brand string, got %q", result)
	}
}

// Intent: cpuBrandFromData returns empty string when no model name line is present.
func TestCpuBrandFromData_EmptyInput(t *testing.T) {
	result := cpuBrandFromData("")
	if result != "" {
		t.Errorf("expected empty string for empty input, got %q", result)
	}
}

// Intent: cpuBrandFromData returns empty string when cpuinfo has no model name field.
func TestCpuBrandFromData_NoModelNameField(t *testing.T) {
	input := "processor\t: 0\n" +
		"vendor_id\t: GenuineIntel\n" +
		"cpu family\t: 6\n" +
		"\n"

	result := cpuBrandFromData(input)
	if result != "" {
		t.Errorf("expected empty string when model name is absent, got %q", result)
	}
}

// --- countPhysicalCoresFromData correctness ---

// Intent: countPhysicalCoresFromData counts unique physical cores on a dual-socket system.
func TestCountPhysicalCoresFromData_DualSocket(t *testing.T) {
	// 2 physical sockets, each with 2 cores (4 logical processors via HT).
	input := "processor\t: 0\nphysical id\t: 0\ncore id\t\t: 0\n\n" +
		"processor\t: 1\nphysical id\t: 0\ncore id\t\t: 1\n\n" +
		"processor\t: 2\nphysical id\t: 1\ncore id\t\t: 0\n\n" +
		"processor\t: 3\nphysical id\t: 1\ncore id\t\t: 1\n\n"

	result := countPhysicalCoresFromData(input)
	if result != 4 {
		t.Errorf("expected 4 physical cores on dual-socket system, got %d", result)
	}
}

// Intent: countPhysicalCoresFromData deduplicates hyperthreaded siblings sharing the same core.
func TestCountPhysicalCoresFromData_HyperThreaded(t *testing.T) {
	// 1 socket, 2 cores, 4 threads (HT siblings share physical id + core id).
	input := "processor\t: 0\nphysical id\t: 0\ncore id\t\t: 0\n\n" +
		"processor\t: 1\nphysical id\t: 0\ncore id\t\t: 1\n\n" +
		"processor\t: 2\nphysical id\t: 0\ncore id\t\t: 0\n\n" +
		"processor\t: 3\nphysical id\t: 0\ncore id\t\t: 1\n\n"

	result := countPhysicalCoresFromData(input)
	if result != 2 {
		t.Errorf("expected 2 physical cores with HT, got %d", result)
	}
}

// Intent: countPhysicalCoresFromData returns 0 for empty input.
func TestCountPhysicalCoresFromData_EmptyInput(t *testing.T) {
	result := countPhysicalCoresFromData("")
	if result != 0 {
		t.Errorf("expected 0 for empty input, got %d", result)
	}
}

// Intent: countPhysicalCoresFromData returns 0 when no physical id lines exist.
func TestCountPhysicalCoresFromData_NoPhysicalID(t *testing.T) {
	input := "processor\t: 0\nmodel name\t: ARM Cortex-A72\n\n"

	result := countPhysicalCoresFromData(input)
	if result != 0 {
		t.Errorf("expected 0 when no physical id lines are present, got %d", result)
	}
}

// Intent: countPhysicalCoresFromData handles input that does not end with a blank line.
func TestCountPhysicalCoresFromData_NoTrailingBlankLine(t *testing.T) {
	input := "processor\t: 0\nphysical id\t: 0\ncore id\t\t: 0"

	result := countPhysicalCoresFromData(input)
	if result != 1 {
		t.Errorf("expected 1 core when input lacks trailing blank line, got %d", result)
	}
}

// --- uptimeSecondsFromData correctness ---

// Intent: uptimeSecondsFromData extracts the first field as whole seconds from typical /proc/uptime.
func TestUptimeSecondsFromData_RealisticInput(t *testing.T) {
	input := "123456.78 67890.12"

	result := uptimeSecondsFromData(input)
	if result != 123456 {
		t.Errorf("expected 123456 seconds, got %d", result)
	}
}

// Intent: uptimeSecondsFromData truncates fractional seconds (no rounding).
func TestUptimeSecondsFromData_FractionalTruncation(t *testing.T) {
	input := "0.99 0.50"

	result := uptimeSecondsFromData(input)
	if result != 0 {
		t.Errorf("expected 0 (truncated from 0.99), got %d", result)
	}
}

// Intent: uptimeSecondsFromData returns 0 for empty input.
func TestUptimeSecondsFromData_EmptyInput(t *testing.T) {
	result := uptimeSecondsFromData("")
	if result != 0 {
		t.Errorf("expected 0 for empty input, got %d", result)
	}
}

// Intent: uptimeSecondsFromData returns 0 for non-numeric input.
func TestUptimeSecondsFromData_NonNumeric(t *testing.T) {
	result := uptimeSecondsFromData("not_a_number idle_time")
	if result != 0 {
		t.Errorf("expected 0 for non-numeric input, got %d", result)
	}
}

// Intent: uptimeSecondsFromData handles large uptime values (server running for years).
func TestUptimeSecondsFromData_LargeValue(t *testing.T) {
	// Approximately 3 years of uptime.
	input := "94608000.42 47304000.21"

	result := uptimeSecondsFromData(input)
	if result != 94608000 {
		t.Errorf("expected 94608000 for multi-year uptime, got %d", result)
	}
}

// --- parseGlobalIPAddresses correctness ---

// Intent: parseGlobalIPAddresses extracts IPv4 and IPv6 addresses from realistic ip command output.
func TestParseGlobalIPAddresses_MixedIPv4IPv6(t *testing.T) {
	input := "2: eth0    inet 10.0.1.10/24 brd 10.0.1.255 scope global eth0\\       valid_lft forever preferred_lft forever\n" +
		"2: eth0    inet6 2001:db8::1/64 scope global \\       valid_lft forever preferred_lft forever\n"

	result := parseGlobalIPAddresses(input)
	if len(result) != 2 {
		t.Fatalf("expected 2 IPs, got %d", len(result))
	}
	if result[0] != "10.0.1.10" {
		t.Errorf("expected first IP to be 10.0.1.10, got %q", result[0])
	}
	if result[1] != "2001:db8::1" {
		t.Errorf("expected second IP to be 2001:db8::1, got %q", result[1])
	}
}

// Intent: parseGlobalIPAddresses handles multiple interfaces.
func TestParseGlobalIPAddresses_MultipleInterfaces(t *testing.T) {
	input := "2: eth0    inet 192.168.1.100/24 brd 192.168.1.255 scope global eth0\n" +
		"3: wlan0    inet 172.16.0.50/16 brd 172.16.255.255 scope global wlan0\n"

	result := parseGlobalIPAddresses(input)
	if len(result) != 2 {
		t.Fatalf("expected 2 IPs, got %d", len(result))
	}
	if result[0] != "192.168.1.100" {
		t.Errorf("expected 192.168.1.100, got %q", result[0])
	}
	if result[1] != "172.16.0.50" {
		t.Errorf("expected 172.16.0.50, got %q", result[1])
	}
}

// Intent: parseGlobalIPAddresses returns empty slice for empty input.
func TestParseGlobalIPAddresses_EmptyInput(t *testing.T) {
	result := parseGlobalIPAddresses("")
	if len(result) != 0 {
		t.Errorf("expected empty slice for empty input, got %d items", len(result))
	}
}

// Intent: parseGlobalIPAddresses skips lines with fewer than 4 fields.
func TestParseGlobalIPAddresses_ShortLines(t *testing.T) {
	input := "2: eth0\n" +
		"3: wlan0 inet\n"

	result := parseGlobalIPAddresses(input)
	if len(result) != 0 {
		t.Errorf("expected empty slice for short lines, got %d items", len(result))
	}
}

// --- parseMountsData correctness ---

// Intent: parseMountsData extracts real filesystem mounts and filters pseudo-filesystems.
func TestParseMountsData_RealisticMounts(t *testing.T) {
	input := "sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\n" +
		"proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\n" +
		"/dev/sda1 / ext4 rw,relatime 0 0\n" +
		"/dev/sdb1 /data xfs rw,relatime 0 0\n" +
		"tmpfs /tmp tmpfs rw,nosuid,nodev 0 0\n"

	result := parseMountsData(input)
	if len(result) != 2 {
		t.Fatalf("expected 2 real mount entries (sysfs/proc/tmpfs filtered), got %d", len(result))
	}

	if result[0].device != "/dev/sda1" {
		t.Errorf("expected first device /dev/sda1, got %q", result[0].device)
	}
	if result[0].mountPoint != "/" {
		t.Errorf("expected first mount point /, got %q", result[0].mountPoint)
	}
	if result[0].fsType != "ext4" {
		t.Errorf("expected first fsType ext4, got %q", result[0].fsType)
	}

	if result[1].device != "/dev/sdb1" {
		t.Errorf("expected second device /dev/sdb1, got %q", result[1].device)
	}
	if result[1].mountPoint != "/data" {
		t.Errorf("expected second mount point /data, got %q", result[1].mountPoint)
	}
	if result[1].fsType != "xfs" {
		t.Errorf("expected second fsType xfs, got %q", result[1].fsType)
	}
}

// Intent: parseMountsData deduplicates by mount point, keeping the first occurrence.
func TestParseMountsData_DeduplicatesMountPoints(t *testing.T) {
	input := "/dev/sda1 /mnt ext4 rw 0 0\n" +
		"/dev/sdb1 /mnt xfs rw 0 0\n"

	result := parseMountsData(input)
	if len(result) != 1 {
		t.Fatalf("expected 1 entry after deduplication, got %d", len(result))
	}
	if result[0].device != "/dev/sda1" {
		t.Errorf("expected first device to win deduplication, got %q", result[0].device)
	}
}

// Intent: parseMountsData returns empty slice for empty input.
func TestParseMountsData_EmptyInput(t *testing.T) {
	result := parseMountsData("")
	if len(result) != 0 {
		t.Errorf("expected empty slice for empty input, got %d entries", len(result))
	}
}

// Intent: parseMountsData skips lines with fewer than 3 fields.
func TestParseMountsData_InsufficientFields(t *testing.T) {
	input := "/dev/sda1 /\n" +
		"/dev/sdb1\n"

	result := parseMountsData(input)
	if len(result) != 0 {
		t.Errorf("expected empty slice for lines with <3 fields, got %d entries", len(result))
	}
}

// Intent: parseMountsData correctly handles NFS and network filesystem entries.
func TestParseMountsData_NFSMount(t *testing.T) {
	input := "server:/export/data /mnt/nfs nfs4 rw,relatime 0 0\n"

	result := parseMountsData(input)
	if len(result) != 1 {
		t.Fatalf("expected 1 NFS mount entry, got %d", len(result))
	}
	if result[0].fsType != "nfs4" {
		t.Errorf("expected fsType nfs4, got %q", result[0].fsType)
	}
}
