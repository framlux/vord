// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"encoding/json"
	"testing"
)

// Fixture /etc/os-release for Ubuntu 24.04.
const fixtureOsReleaseUbuntu = `PRETTY_NAME="Ubuntu 24.04.1 LTS"
NAME="Ubuntu"
VERSION_ID="24.04"
VERSION="24.04.1 LTS (Noble Numbat)"
VERSION_CODENAME=noble
ID=ubuntu
ID_LIKE=debian
HOME_URL="https://www.ubuntu.com/"
SUPPORT_URL="https://help.ubuntu.com/"
BUG_REPORT_URL="https://bugs.launchpad.net/ubuntu/"
PRIVACY_POLICY_URL="https://www.ubuntu.com/legal/terms-and-policies/privacy-policy"
UBUNTU_CODENAME=noble
LOGO=ubuntu-logo
`

// Fixture /etc/os-release for Rocky Linux 9.
const fixtureOsReleaseRocky = `NAME="Rocky Linux"
VERSION="9.3 (Blue Onyx)"
ID="rocky"
ID_LIKE="rhel centos fedora"
VERSION_ID="9.3"
PLATFORM_ID="platform:el9"
PRETTY_NAME="Rocky Linux 9.3 (Blue Onyx)"
ANSI_COLOR="0;32"
LOGO="fedora-logo-icon"
CPE_NAME="cpe:/o:rocky:rocky:9::baseos"
HOME_URL="https://rockylinux.org/"
BUG_REPORT_URL="https://bugs.rockylinux.org/"
SUPPORT_END="2032-05-31"
ROCKY_SUPPORT_PRODUCT="Rocky-Linux-9"
ROCKY_SUPPORT_PRODUCT_VERSION="9.3"
REDHAT_SUPPORT_PRODUCT="Rocky Linux"
REDHAT_SUPPORT_PRODUCT_VERSION="9.3"
`

// Fixture /etc/os-release for Debian 12 with BUILD_ID.
const fixtureOsReleaseDebian = `PRETTY_NAME="Debian GNU/Linux 12 (bookworm)"
NAME="Debian GNU/Linux"
VERSION_ID="12"
VERSION="12 (bookworm)"
VERSION_CODENAME=bookworm
ID=debian
HOME_URL="https://www.debian.org/"
SUPPORT_URL="https://www.debian.org/support"
BUG_REPORT_URL="https://bugs.debian.org/"
BUILD_ID="2024-01-15"
`

// Fixture with quoted and unquoted values.
const fixtureOsReleaseMixed = `NAME="Fedora Linux"
VERSION_ID=41
ID=fedora
VERSION_CODENAME=""
`

func TestParseOsReleaseUbuntu(t *testing.T) {
	osRelease := parseOsReleaseData(fixtureOsReleaseUbuntu)

	tests := []struct {
		key  string
		want string
	}{
		{"NAME", "Ubuntu"},
		{"VERSION_ID", "24.04"},
		{"VERSION_CODENAME", "noble"},
		{"ID", "ubuntu"},
		{"ID_LIKE", "debian"},
	}

	for _, tt := range tests {
		t.Run(tt.key, func(t *testing.T) {
			got := osRelease[tt.key]
			if got != tt.want {
				t.Errorf("osRelease[%q] = %q, want %q", tt.key, got, tt.want)
			}
		})
	}
}

func TestParseOsReleaseRocky(t *testing.T) {
	osRelease := parseOsReleaseData(fixtureOsReleaseRocky)

	if osRelease["NAME"] != "Rocky Linux" {
		t.Errorf("NAME = %q, want 'Rocky Linux'", osRelease["NAME"])
	}
	if osRelease["VERSION_ID"] != "9.3" {
		t.Errorf("VERSION_ID = %q, want '9.3'", osRelease["VERSION_ID"])
	}
	if osRelease["ID"] != "rocky" {
		t.Errorf("ID = %q, want 'rocky'", osRelease["ID"])
	}
}

func TestParseOsReleaseDebian(t *testing.T) {
	osRelease := parseOsReleaseData(fixtureOsReleaseDebian)

	if osRelease["VERSION_CODENAME"] != "bookworm" {
		t.Errorf("VERSION_CODENAME = %q, want 'bookworm'", osRelease["VERSION_CODENAME"])
	}
	if osRelease["BUILD_ID"] != "2024-01-15" {
		t.Errorf("BUILD_ID = %q, want '2024-01-15'", osRelease["BUILD_ID"])
	}
	if osRelease["VERSION_ID"] != "12" {
		t.Errorf("VERSION_ID = %q, want '12'", osRelease["VERSION_ID"])
	}
}

func TestParseOsReleaseMixedQuoting(t *testing.T) {
	osRelease := parseOsReleaseData(fixtureOsReleaseMixed)

	// Quoted value should have quotes stripped.
	if osRelease["NAME"] != "Fedora Linux" {
		t.Errorf("NAME = %q, want 'Fedora Linux'", osRelease["NAME"])
	}
	// Unquoted value should remain as-is.
	if osRelease["VERSION_ID"] != "41" {
		t.Errorf("VERSION_ID = %q, want '41'", osRelease["VERSION_ID"])
	}
	// Empty quoted value should be empty string.
	if osRelease["VERSION_CODENAME"] != "" {
		t.Errorf("VERSION_CODENAME = %q, want empty string", osRelease["VERSION_CODENAME"])
	}
}

func TestParseOsReleaseEmpty(t *testing.T) {
	osRelease := parseOsReleaseData("")
	if len(osRelease) != 0 {
		t.Errorf("empty input should produce empty map, got %d entries", len(osRelease))
	}
}

func TestParseOsReleaseMalformedLines(t *testing.T) {
	data := `this line has no equals sign
NAME="Test"
another bad line
VERSION_ID=1.0
`
	osRelease := parseOsReleaseData(data)
	if osRelease["NAME"] != "Test" {
		t.Errorf("NAME = %q, want 'Test'", osRelease["NAME"])
	}
	if osRelease["VERSION_ID"] != "1.0" {
		t.Errorf("VERSION_ID = %q, want '1.0'", osRelease["VERSION_ID"])
	}
	// Lines without = should be skipped, so map should have exactly 2 entries.
	if len(osRelease) != 2 {
		t.Errorf("got %d entries, want 2", len(osRelease))
	}
}

func TestParseVersion(t *testing.T) {
	tests := []struct {
		version   string
		wantMajor int
		wantMinor int
		wantPatch int
	}{
		{"24.04", 24, 4, 0},
		{"9.3", 9, 3, 0},
		{"12", 12, 0, 0},
		{"1.2.3", 1, 2, 3},
		{"", 0, 0, 0},
		{"22.04.1", 22, 4, 1},
		{"0.0.0", 0, 0, 0},
		{"100.200.300", 100, 200, 300},
	}

	for _, tt := range tests {
		t.Run(tt.version, func(t *testing.T) {
			major, minor, patch := parseVersion(tt.version)
			if major != tt.wantMajor {
				t.Errorf("major = %d, want %d", major, tt.wantMajor)
			}
			if minor != tt.wantMinor {
				t.Errorf("minor = %d, want %d", minor, tt.wantMinor)
			}
			if patch != tt.wantPatch {
				t.Errorf("patch = %d, want %d", patch, tt.wantPatch)
			}
		})
	}
}

func TestParseVersionNonNumeric(t *testing.T) {
	// strconv.Atoi returns 0 for non-numeric strings, which is the expected behavior.
	major, minor, patch := parseVersion("abc.def.ghi")
	if major != 0 || minor != 0 || patch != 0 {
		t.Errorf("parseVersion('abc.def.ghi') = (%d, %d, %d), want (0, 0, 0)", major, minor, patch)
	}
}

func TestOsVersionPayloadJSON(t *testing.T) {
	payload := osVersionPayload{
		Name:     "Ubuntu",
		Version:  "24.04",
		Major:    24,
		Minor:    4,
		Patch:    0,
		Build:    "6.8.0-45-generic",
		Platform: "ubuntu",
		Codename: "noble",
		Arch:     "amd64",
		Extra:    "Linux version 6.8.0-45-generic",
		Revision: "",
	}

	data, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("json.Marshal() error: %v", err)
	}

	var decoded osVersionPayload
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatalf("json.Unmarshal() error: %v", err)
	}

	if decoded.Name != "Ubuntu" {
		t.Errorf("Name = %q, want 'Ubuntu'", decoded.Name)
	}
	if decoded.Major != 24 {
		t.Errorf("Major = %d, want 24", decoded.Major)
	}
	if decoded.Minor != 4 {
		t.Errorf("Minor = %d, want 4", decoded.Minor)
	}
	if decoded.Platform != "ubuntu" {
		t.Errorf("Platform = %q, want 'ubuntu'", decoded.Platform)
	}
	if decoded.Codename != "noble" {
		t.Errorf("Codename = %q, want 'noble'", decoded.Codename)
	}
}

func TestOsVersionFullPipeline(t *testing.T) {
	// End-to-end test: parse os-release fixture, parse version, build payload.
	osRelease := parseOsReleaseData(fixtureOsReleaseUbuntu)

	version := osRelease["VERSION_ID"]
	major, minor, patch := parseVersion(version)

	payload := osVersionPayload{
		Name:     osRelease["NAME"],
		Version:  version,
		Major:    major,
		Minor:    minor,
		Patch:    patch,
		Platform: osRelease["ID"],
		Codename: osRelease["VERSION_CODENAME"],
		Arch:     "amd64",
	}

	if payload.Name != "Ubuntu" {
		t.Errorf("Name = %q, want 'Ubuntu'", payload.Name)
	}
	if payload.Version != "24.04" {
		t.Errorf("Version = %q, want '24.04'", payload.Version)
	}
	if payload.Major != 24 {
		t.Errorf("Major = %d, want 24", payload.Major)
	}
	if payload.Minor != 4 {
		t.Errorf("Minor = %d, want 4", payload.Minor)
	}
	if payload.Patch != 0 {
		t.Errorf("Patch = %d, want 0", payload.Patch)
	}
	if payload.Platform != "ubuntu" {
		t.Errorf("Platform = %q, want 'ubuntu'", payload.Platform)
	}
	if payload.Codename != "noble" {
		t.Errorf("Codename = %q, want 'noble'", payload.Codename)
	}
}
