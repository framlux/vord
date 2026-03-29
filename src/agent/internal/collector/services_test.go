// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"testing"
)

// Intent: Standard systemctl output with multiple services is parsed correctly.
func TestParseSystemctlListUnits_MultipleServices(t *testing.T) {
	output := `UNIT                    LOAD   ACTIVE SUB     DESCRIPTION
sshd.service            loaded active running OpenBSD Secure Shell server
nginx.service           loaded active running A high performance web server
cron.service            loaded active running Regular background program processing daemon

3 loaded units listed.
`
	services := parseSystemctlListUnits(output)

	if len(services) != 3 {
		t.Fatalf("expected 3 services, got %d", len(services))
	}
	if services[0].Unit != "sshd.service" {
		t.Errorf("Unit = %q, want %q", services[0].Unit, "sshd.service")
	}
	if services[0].LoadState != "loaded" {
		t.Errorf("LoadState = %q, want %q", services[0].LoadState, "loaded")
	}
	if services[0].ActiveState != "active" {
		t.Errorf("ActiveState = %q, want %q", services[0].ActiveState, "active")
	}
	if services[0].SubState != "running" {
		t.Errorf("SubState = %q, want %q", services[0].SubState, "running")
	}
	if services[0].Description != "OpenBSD Secure Shell server" {
		t.Errorf("Description = %q, want %q", services[0].Description, "OpenBSD Secure Shell server")
	}
}

// Intent: Lines starting with "UNIT" header are skipped.
func TestParseSystemctlListUnits_SkipsHeader(t *testing.T) {
	output := `UNIT                    LOAD   ACTIVE SUB     DESCRIPTION
sshd.service            loaded active running OpenBSD Secure Shell server
`
	services := parseSystemctlListUnits(output)

	if len(services) != 1 {
		t.Fatalf("expected 1 service (header skipped), got %d", len(services))
	}
}

// Intent: Footer line "loaded units listed" is skipped.
func TestParseSystemctlListUnits_SkipsFooter(t *testing.T) {
	output := `sshd.service            loaded active running OpenBSD Secure Shell server

1 loaded units listed. To show all installed unit files use 'systemctl list-unit-files'.
To show all installed unit files use 'systemctl list-unit-files'.
`
	services := parseSystemctlListUnits(output)

	if len(services) != 1 {
		t.Fatalf("expected 1 service (footer skipped), got %d", len(services))
	}
}

// Intent: Non-service units (e.g., .timer, .socket) are skipped.
func TestParseSystemctlListUnits_SkipsNonService(t *testing.T) {
	output := `sshd.service            loaded active running OpenBSD Secure Shell server
apt-daily.timer         loaded active waiting Daily apt activities
docker.socket           loaded active listening Docker Socket for the API
`
	services := parseSystemctlListUnits(output)

	if len(services) != 1 {
		t.Errorf("expected 1 service (non-service skipped), got %d", len(services))
	}
	if services[0].Unit != "sshd.service" {
		t.Errorf("Unit = %q, want %q", services[0].Unit, "sshd.service")
	}
}

// Intent: Empty output returns nil.
func TestParseSystemctlListUnits_EmptyOutput(t *testing.T) {
	services := parseSystemctlListUnits("")

	if services != nil {
		t.Errorf("expected nil, got %v", services)
	}
}

// Intent: Lines with fewer than 4 fields are skipped.
func TestParseSystemctlListUnits_ShortFields(t *testing.T) {
	output := "sshd.service loaded active\n"
	services := parseSystemctlListUnits(output)

	if len(services) != 0 {
		t.Errorf("expected 0 services for short fields, got %d", len(services))
	}
}

// Intent: Description is assembled from fields[4:] joined with spaces.
func TestParseSystemctlListUnits_DescriptionAssembly(t *testing.T) {
	output := "docker.service loaded active running Docker Application Container Engine\n"
	services := parseSystemctlListUnits(output)

	if len(services) != 1 {
		t.Fatalf("expected 1 service, got %d", len(services))
	}
	if services[0].Description != "Docker Application Container Engine" {
		t.Errorf("Description = %q, want %q", services[0].Description, "Docker Application Container Engine")
	}
}
