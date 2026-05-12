// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
)

type systemInfoPayload struct {
	Hostname         string   `json:"hostname"`
	UUID             string   `json:"uuid"`
	CPUType          string   `json:"cpu_type"`
	CPUBrand         string   `json:"cpu_brand"`
	CPUPhysicalCores int      `json:"cpu_physical_cores"`
	CPULogicalCores  int      `json:"cpu_logical_cores"`
	PhysicalMemory   int64    `json:"physical_memory"`
	HardwareVendor   string   `json:"hardware_vendor"`
	HardwareModel    string   `json:"hardware_model"`
	HardwareVersion  string   `json:"hardware_version"`
	HardwareSerial   string   `json:"hardware_serial"`
	BoardVendor      string   `json:"board_vendor"`
	BoardModel       string   `json:"board_model"`
	BoardVersion     string   `json:"board_version"`
	BoardSerial      string   `json:"board_serial"`
	ComputerName     string   `json:"computer_name"`
	LocalHostname    string   `json:"local_hostname"`
	UptimeSeconds    int64    `json:"uptime_seconds"`
	BiosVersion      string   `json:"bios_version"`
	IpAddresses      []string `json:"ip_addresses"`
}

func readFileTrimmed(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}

	return strings.TrimSpace(string(data))
}

func readDMI(field string) string {
	return readFileTrimmed("/sys/class/dmi/id/" + filepath.Base(field))
}

// cpuBrandFromData extracts the CPU brand string from /proc/cpuinfo content.
func cpuBrandFromData(data string) string {
	for _, line := range strings.Split(data, "\n") {
		if strings.HasPrefix(line, "model name") {
			parts := strings.SplitN(line, ":", 2)
			if len(parts) == 2 {
				return strings.TrimSpace(parts[1])
			}
		}
	}

	return ""
}

// countPhysicalCoresFromData counts unique physical cores from /proc/cpuinfo content.
// Returns 0 if no physical id/core id pairs are found.
func countPhysicalCoresFromData(data string) int {
	seen := make(map[string]bool)
	var physID, coreID string
	for _, line := range strings.Split(data, "\n") {
		if strings.HasPrefix(line, "physical id") {
			parts := strings.SplitN(line, ":", 2)
			if len(parts) == 2 {
				physID = strings.TrimSpace(parts[1])
			}
		} else if strings.HasPrefix(line, "core id") {
			parts := strings.SplitN(line, ":", 2)
			if len(parts) == 2 {
				coreID = strings.TrimSpace(parts[1])
			}
		} else if line == "" && physID != "" {
			key := physID + ":" + coreID
			seen[key] = true
			physID, coreID = "", ""
		}
	}

	// Record final block if file doesn't end with a blank line.
	if physID != "" {
		key := physID + ":" + coreID
		seen[key] = true
	}

	return len(seen)
}

// memTotalBytesFromData extracts MemTotal from /proc/meminfo content and returns it in bytes.
func memTotalBytesFromData(data string) int64 {
	for _, line := range strings.Split(data, "\n") {
		if strings.HasPrefix(line, "MemTotal:") {
			var val int64
			_, err := fmt.Sscanf(line, "MemTotal: %d kB", &val)
			if err == nil {
				return val * 1024
			}
		}
	}

	return 0
}

// uptimeSecondsFromData parses /proc/uptime content and returns the uptime in seconds.
func uptimeSecondsFromData(data string) int64 {
	fields := strings.Fields(data)
	if len(fields) == 0 {
		return 0
	}
	var secs float64
	_, err := fmt.Sscanf(fields[0], "%f", &secs)
	if err != nil {
		return 0
	}

	return int64(secs)
}

// parseGlobalIPAddresses parses the output of `ip -o addr show scope global`
// and returns the list of IP addresses.
func parseGlobalIPAddresses(data string) []string {
	var ips []string
	for _, line := range strings.Split(data, "\n") {
		fields := strings.Fields(line)
		if len(fields) < 4 {
			continue
		}
		// Field 3 is "addr/prefix", e.g. "10.0.1.10/24" or "fd00::1/64"
		cidr := fields[3]
		parts := strings.SplitN(cidr, "/", 2)
		if len(parts) > 0 && parts[0] != "" {
			ips = append(ips, parts[0])
		}
	}

	return ips
}

func globalIPAddresses(ctx context.Context) []string {
	out, err := runCmd(ctx, "ip", "-o", "addr", "show", "scope", "global")
	if err != nil {
		return nil
	}

	return parseGlobalIPAddresses(string(out))
}

// isPlaceholderSerial returns true if the serial string is empty or a
// well-known placeholder value that hardware vendors use for unfilled fields.
func isPlaceholderSerial(serial string) bool {
	return serial == "" || serial == "Not Specified" || serial == "To Be Filled By O.E.M."
}

func hardwareSerial(ctx context.Context) string {
	serial := readDMI("product_serial")
	if isPlaceholderSerial(serial) == false {
		return serial
	}
	// Fallback to dmidecode.
	out, err := runCmd(ctx, "dmidecode", "-s", "system-serial-number")
	if err != nil {
		slog.Debug("dmidecode fallback failed", "error", err)

		return ""
	}
	result := strings.TrimSpace(string(out))
	if isPlaceholderSerial(result) {
		return ""
	}

	return result
}
