// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"strconv"
	"strings"
)

type osVersionPayload struct {
	Name     string `json:"name"`
	Version  string `json:"version"`
	Major    int    `json:"major"`
	Minor    int    `json:"minor"`
	Patch    int    `json:"patch"`
	Build    string `json:"build"`
	Platform string `json:"platform"`
	Codename string `json:"codename"`
	Arch     string `json:"arch"`
	Extra    string `json:"extra"`
	Revision string `json:"revision"`
}

// parseOsReleaseData parses /etc/os-release content and returns key-value pairs.
func parseOsReleaseData(data string) map[string]string {
	result := make(map[string]string)

	for _, line := range strings.Split(data, "\n") {
		parts := strings.SplitN(line, "=", 2)
		if len(parts) == 2 {
			key := parts[0]
			value := strings.Trim(parts[1], "\"")
			result[key] = value
		}
	}

	return result
}

func parseVersion(version string) (int, int, int) {
	parts := strings.Split(version, ".")
	var major, minor, patch int
	if len(parts) >= 1 {
		major, _ = strconv.Atoi(parts[0])
	}
	if len(parts) >= 2 {
		minor, _ = strconv.Atoi(parts[1])
	}
	if len(parts) >= 3 {
		patch, _ = strconv.Atoi(parts[2])
	}

	return major, minor, patch
}
