// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Fixture /proc/uptime content.
const fixtureProcUptime = `123456.78 234567.89
`

// Fixture /proc/uptime with fractional seconds.
const fixtureProcUptimeFractional = `0.50 0.50
`

func TestUptimeSeconds(t *testing.T) {
	got := uptimeSecondsFromData(fixtureProcUptime)
	if got != 123456 {
		t.Errorf("uptimeSeconds = %d, want 123456", got)
	}
}

func TestUptimeSecondsFractional(t *testing.T) {
	// 0.50 truncated to int64 is 0.
	got := uptimeSecondsFromData(fixtureProcUptimeFractional)
	if got != 0 {
		t.Errorf("uptimeSeconds(0.50) = %d, want 0", got)
	}
}

func TestUptimeSecondsEmpty(t *testing.T) {
	got := uptimeSecondsFromData("")
	if got != 0 {
		t.Errorf("uptimeSeconds(empty) = %d, want 0", got)
	}
}

func TestUptimeSecondsMalformed(t *testing.T) {
	got := uptimeSecondsFromData("not_a_number other_field")
	if got != 0 {
		t.Errorf("uptimeSeconds(malformed) = %d, want 0", got)
	}
}

func TestUptimeSecondsLargeValue(t *testing.T) {
	// ~1 year uptime.
	got := uptimeSecondsFromData("31536000.00 63072000.00")
	if got != 31536000 {
		t.Errorf("uptimeSeconds = %d, want 31536000", got)
	}
}

func TestReadFileTrimmedWithDMIFixtures(t *testing.T) {
	dir := t.TempDir()

	// Simulate DMI files under /sys/class/dmi/id/.
	dmiDir := filepath.Join(dir, "sys", "class", "dmi", "id")
	if err := os.MkdirAll(dmiDir, 0755); err != nil {
		t.Fatal(err)
	}

	tests := []struct {
		filename string
		content  string
		want     string
	}{
		{"sys_vendor", "Dell Inc.\n", "Dell Inc."},
		{"product_name", "PowerEdge R640\n", "PowerEdge R640"},
		{"product_version", "  v1.0  \n", "v1.0"},
		{"board_vendor", "Dell Inc.\n", "Dell Inc."},
		{"board_name", "0T7D40\n", "0T7D40"},
		{"bios_version", "2.12.2\n", "2.12.2"},
	}

	for _, tt := range tests {
		t.Run(tt.filename, func(t *testing.T) {
			path := filepath.Join(dmiDir, tt.filename)
			if err := os.WriteFile(path, []byte(tt.content), 0644); err != nil {
				t.Fatal(err)
			}
			got := readFileTrimmed(path)
			if got != tt.want {
				t.Errorf("readFileTrimmed(%s) = %q, want %q", tt.filename, got, tt.want)
			}
		})
	}
}

func TestGlobalIPAddresses(t *testing.T) {
	// Simulated output of: ip -4 -o addr show scope global
	fixture := `2: eth0    inet 10.0.1.10/24 brd 10.0.1.255 scope global eth0\       valid_lft forever preferred_lft forever
3: eth1    inet 192.168.1.100/24 brd 192.168.1.255 scope global eth1\       valid_lft forever preferred_lft forever
`
	ips := parseGlobalIPAddresses(fixture)

	if len(ips) != 2 {
		t.Fatalf("got %d IPs, want 2", len(ips))
	}
	if ips[0] != "10.0.1.10" {
		t.Errorf("ips[0] = %q, want '10.0.1.10'", ips[0])
	}
	if ips[1] != "192.168.1.100" {
		t.Errorf("ips[1] = %q, want '192.168.1.100'", ips[1])
	}
}

func TestGlobalIPAddressesSingleInterface(t *testing.T) {
	fixture := `2: ens5    inet 172.31.42.195/20 brd 172.31.47.255 scope global dynamic ens5\       valid_lft 2345sec preferred_lft 2345sec
`
	ips := parseGlobalIPAddresses(fixture)

	if len(ips) != 1 {
		t.Fatalf("got %d IPs, want 1", len(ips))
	}
	if ips[0] != "172.31.42.195" {
		t.Errorf("ips[0] = %q, want '172.31.42.195'", ips[0])
	}
}

func TestGlobalIPAddressesEmpty(t *testing.T) {
	ips := parseGlobalIPAddresses("")
	if len(ips) != 0 {
		t.Errorf("got %d IPs from empty input, want 0", len(ips))
	}
}

func TestGlobalIPAddressesShortLines(t *testing.T) {
	fixture := `short line
another short
`
	ips := parseGlobalIPAddresses(fixture)
	if len(ips) != 0 {
		t.Errorf("got %d IPs from short lines, want 0", len(ips))
	}
}

func TestIsPlaceholderSerial(t *testing.T) {
	tests := []struct {
		serial        string
		isPlaceholder bool
	}{
		{"", true},
		{"Not Specified", true},
		{"To Be Filled By O.E.M.", true},
		{"ABC123DEF456", false},
		{"SN-12345678", false},
		{"VMware-56 4d 7e 8c", false},
		{"0", false},
		{" ", false},
	}

	for _, tt := range tests {
		name := tt.serial
		if name == "" {
			name = "(empty)"
		}
		t.Run(name, func(t *testing.T) {
			got := isPlaceholderSerial(tt.serial)
			if got != tt.isPlaceholder {
				t.Errorf("isPlaceholderSerial(%q) = %v, want %v", tt.serial, got, tt.isPlaceholder)
			}
		})
	}
}

func TestSystemInfoPayloadJSON(t *testing.T) {
	payload := systemInfoPayload{
		Hostname:         "server01",
		UUID:             "550e8400-e29b-41d4-a716-446655440000",
		CPUType:          "amd64",
		CPUBrand:         "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz",
		CPUPhysicalCores: 20,
		CPULogicalCores:  40,
		PhysicalMemory:   34359738368,
		HardwareVendor:   "Dell Inc.",
		HardwareModel:    "PowerEdge R640",
		HardwareVersion:  "",
		HardwareSerial:   "ABC123DEF",
		BoardVendor:      "Dell Inc.",
		BoardModel:       "0T7D40",
		BoardVersion:     "A00",
		BoardSerial:      ".ABC123DEF.CN123",
		ComputerName:     "server01",
		LocalHostname:    "server01",
		UptimeSeconds:    123456,
		BiosVersion:      "2.12.2",
		IpAddresses:      []string{"10.0.1.10", "192.168.1.100"},
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded systemInfoPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.Hostname != "server01" {
		t.Errorf("Hostname = %q, want 'server01'", decoded.Hostname)
	}
	if decoded.CPUPhysicalCores != 20 {
		t.Errorf("CPUPhysicalCores = %d, want 20", decoded.CPUPhysicalCores)
	}
	if decoded.CPULogicalCores != 40 {
		t.Errorf("CPULogicalCores = %d, want 40", decoded.CPULogicalCores)
	}
	if decoded.PhysicalMemory != 34359738368 {
		t.Errorf("PhysicalMemory = %d, want 34359738368", decoded.PhysicalMemory)
	}
	if len(decoded.IpAddresses) != 2 {
		t.Fatalf("IpAddresses has %d entries, want 2", len(decoded.IpAddresses))
	}
	if decoded.IpAddresses[0] != "10.0.1.10" {
		t.Errorf("IpAddresses[0] = %q, want '10.0.1.10'", decoded.IpAddresses[0])
	}
}

func TestSystemInfoPayloadJSONFieldNames(t *testing.T) {
	payload := systemInfoPayload{
		Hostname:    "test",
		IpAddresses: []string{"1.2.3.4"},
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	jsonStr := string(data)
	expectedFields := []string{
		"hostname", "uuid", "cpu_type", "cpu_brand",
		"cpu_physical_cores", "cpu_logical_cores", "physical_memory",
		"hardware_vendor", "hardware_model", "hardware_version",
		"hardware_serial", "board_vendor", "board_model",
		"board_version", "board_serial", "computer_name",
		"local_hostname", "uptime_seconds", "bios_version",
		"ip_addresses",
	}

	for _, field := range expectedFields {
		if !strings.Contains(jsonStr, field) {
			t.Errorf("JSON output missing field %q", field)
		}
	}
}

func TestSystemInfoPayloadNilIPs(t *testing.T) {
	payload := systemInfoPayload{
		Hostname:    "test",
		IpAddresses: nil,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded systemInfoPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.IpAddresses != nil {
		t.Errorf("IpAddresses should be nil, got %v", decoded.IpAddresses)
	}
}
