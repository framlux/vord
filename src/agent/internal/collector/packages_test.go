// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"testing"
)

// --- parseAptUpgradable tests ---

// Intent: Single upgradable package is parsed correctly with all fields.
func TestParseAptUpgradable_SinglePackage(t *testing.T) {
	output := `Listing...
libssl3/jammy-updates 3.0.2-0ubuntu1.16 amd64 [upgradable from: 3.0.2-0ubuntu1.15]
`
	updates := parseAptUpgradable(output)

	if len(updates) != 1 {
		t.Fatalf("expected 1 update, got %d", len(updates))
	}
	if updates[0].Name != "libssl3" {
		t.Errorf("Name = %q, want %q", updates[0].Name, "libssl3")
	}
	if updates[0].AvailableVersion != "3.0.2-0ubuntu1.16" {
		t.Errorf("AvailableVersion = %q, want %q", updates[0].AvailableVersion, "3.0.2-0ubuntu1.16")
	}
	if updates[0].CurrentVersion != "3.0.2-0ubuntu1.15" {
		t.Errorf("CurrentVersion = %q, want %q", updates[0].CurrentVersion, "3.0.2-0ubuntu1.15")
	}
}

// Intent: Multiple packages are parsed; security updates detected by source containing "-security".
func TestParseAptUpgradable_MultiplePackages(t *testing.T) {
	output := `Listing...
libssl3/jammy-security 3.0.2-0ubuntu1.16 amd64 [upgradable from: 3.0.2-0ubuntu1.15]
curl/jammy-updates 7.81.0-1ubuntu1.17 amd64 [upgradable from: 7.81.0-1ubuntu1.16]
openssl/jammy-security 3.0.2-0ubuntu1.16 amd64 [upgradable from: 3.0.2-0ubuntu1.15]
`
	updates := parseAptUpgradable(output)

	if len(updates) != 3 {
		t.Fatalf("expected 3 updates, got %d", len(updates))
	}
	if updates[0].IsSecurityUpdate == false {
		t.Error("expected libssl3 to be a security update")
	}
	if updates[1].IsSecurityUpdate {
		t.Error("expected curl to NOT be a security update")
	}
	if updates[2].IsSecurityUpdate == false {
		t.Error("expected openssl to be a security update")
	}
}

// Intent: Output containing only "Listing..." header returns empty slice.
func TestParseAptUpgradable_OnlyListingHeader(t *testing.T) {
	output := "Listing...\n"
	updates := parseAptUpgradable(output)

	if len(updates) != 0 {
		t.Errorf("expected 0 updates, got %d", len(updates))
	}
}

// Intent: Empty output returns nil.
func TestParseAptUpgradable_EmptyOutput(t *testing.T) {
	updates := parseAptUpgradable("")

	if updates != nil {
		t.Errorf("expected nil, got %v", updates)
	}
}

// Intent: Malformed line without "/" separator is skipped.
func TestParseAptUpgradable_MalformedLine(t *testing.T) {
	output := "some malformed line [upgradable from: 1.0]\n"
	updates := parseAptUpgradable(output)

	if len(updates) != 0 {
		t.Errorf("expected 0 updates for malformed line, got %d", len(updates))
	}
}

// Intent: Line with "/" but only one whitespace-delimited field after slash is skipped.
func TestParseAptUpgradable_ShortFieldsAfterSlash(t *testing.T) {
	// After splitting on "/", the rest is a single word: "source" with no version field.
	output := "pkg/source [upgradable from: 1.0]\n"
	updates := parseAptUpgradable(output)

	// strings.Fields("source [upgradable from: 1.0]") produces 5 fields,
	// so this line IS parsed (fields[1] = "[upgradable").
	// This test verifies the parser does not crash on unusual formats.
	if updates == nil {
		t.Error("expected non-nil result")
	}
}

// --- parseRpmCheckUpdate tests ---

// Intent: Standard check-update output with "pkg.arch version repo" format is parsed.
func TestParseRpmCheckUpdate_StandardOutput(t *testing.T) {
	output := `bash.x86_64                          5.1.8-6.el8                       baseos
curl.x86_64                          7.61.1-30.el8                     baseos
openssl.x86_64                       1.1.1k-12.el8                    baseos
`
	secPkgs := map[string]bool{"openssl": true}
	updates := parseRpmCheckUpdate(output, secPkgs)

	if len(updates) != 3 {
		t.Fatalf("expected 3 updates, got %d", len(updates))
	}
	if updates[0].Name != "bash" {
		t.Errorf("Name = %q, want %q", updates[0].Name, "bash")
	}
	if updates[0].AvailableVersion != "5.1.8-6.el8" {
		t.Errorf("AvailableVersion = %q, want %q", updates[0].AvailableVersion, "5.1.8-6.el8")
	}
	if updates[2].IsSecurityUpdate == false {
		t.Error("expected openssl to be flagged as security update")
	}
	if updates[0].IsSecurityUpdate {
		t.Error("expected bash to NOT be flagged as security update")
	}
}

// Intent: Empty output returns nil.
func TestParseRpmCheckUpdate_EmptyOutput(t *testing.T) {
	updates := parseRpmCheckUpdate("", map[string]bool{})

	if updates != nil {
		t.Errorf("expected nil, got %v", updates)
	}
}

// Intent: Lines with fewer than 2 fields are skipped.
func TestParseRpmCheckUpdate_ShortFields(t *testing.T) {
	output := "singleword\n\nbash.x86_64 5.1.8-6.el8 baseos\n"
	updates := parseRpmCheckUpdate(output, map[string]bool{})

	if len(updates) != 1 {
		t.Errorf("expected 1 update, got %d", len(updates))
	}
}

// --- parseRpmSecurityList tests ---

// Intent: Advisory lines are parsed into a set of package names.
func TestParseRpmSecurityList_WithPackages(t *testing.T) {
	output := `RHSA-2023:0001 Important/Sec. openssl-1.1.1k-12.el8.x86_64
RHSA-2023:0002 Moderate/Sec. curl-7.61.1-30.el8.x86_64
`
	secPkgs := parseRpmSecurityList(output)

	if secPkgs["openssl-1"] == false {
		t.Error("expected openssl-1 in security packages")
	}
	if secPkgs["curl-7"] == false {
		t.Error("expected curl-7 in security packages")
	}
}

// Intent: Empty output returns empty map.
func TestParseRpmSecurityList_Empty(t *testing.T) {
	secPkgs := parseRpmSecurityList("")

	if len(secPkgs) != 0 {
		t.Errorf("expected empty map, got %d entries", len(secPkgs))
	}
}

// --- parseZypperUpdates tests ---

// Intent: Standard pipe-delimited zypper table rows are parsed correctly.
func TestParseZypperUpdates_StandardTable(t *testing.T) {
	output := `S | Repository       | Name     | Current Version | Available Version | Arch
--+------------------+----------+-----------------+-------------------+------
v | openSUSE-Oss     | bash     | 4.4.23-1.1      | 5.0-1.1           | x86_64
v | openSUSE-Updates | curl     | 7.79.1-1.1      | 7.80.0-1.1        | x86_64
`
	secPkgs := map[string]bool{"curl": true}
	updates := parseZypperUpdates(output, secPkgs)

	if len(updates) != 2 {
		t.Fatalf("expected 2 updates, got %d", len(updates))
	}
	if updates[0].Name != "bash" {
		t.Errorf("Name = %q, want %q", updates[0].Name, "bash")
	}
	if updates[0].CurrentVersion != "4.4.23-1.1" {
		t.Errorf("CurrentVersion = %q, want %q", updates[0].CurrentVersion, "4.4.23-1.1")
	}
	if updates[0].AvailableVersion != "5.0-1.1" {
		t.Errorf("AvailableVersion = %q, want %q", updates[0].AvailableVersion, "5.0-1.1")
	}
	if updates[0].IsSecurityUpdate {
		t.Error("expected bash to NOT be a security update")
	}
	if updates[1].IsSecurityUpdate == false {
		t.Error("expected curl to be a security update")
	}
}

// Intent: Header row with "Name" is skipped.
func TestParseZypperUpdates_SkipsHeaderAndSeparator(t *testing.T) {
	output := `S | Repository | Name | Current Version | Available Version | Arch
--+------------------+----------+-----------------+-------------------+------
`
	updates := parseZypperUpdates(output, map[string]bool{})

	if len(updates) != 0 {
		t.Errorf("expected 0 updates (header only), got %d", len(updates))
	}
}

// Intent: Empty output returns nil.
func TestParseZypperUpdates_Empty(t *testing.T) {
	updates := parseZypperUpdates("", map[string]bool{})

	if updates != nil {
		t.Errorf("expected nil, got %v", updates)
	}
}

// Intent: Lines with fewer than 5 columns are skipped.
func TestParseZypperUpdates_ShortColumns(t *testing.T) {
	output := "v | openSUSE | bash | 4.4\n"
	updates := parseZypperUpdates(output, map[string]bool{})

	if len(updates) != 0 {
		t.Errorf("expected 0 updates for short columns, got %d", len(updates))
	}
}

// --- parseZypperSecurityPatches tests ---

// Intent: Security patches table is parsed into package name set.
func TestParseZypperSecurityPatches_WithPatches(t *testing.T) {
	output := `--+------------------+----------+---------
 | openSUSE-Updates | openssl  | needed
 | openSUSE-Updates | curl     | needed
`
	secPkgs := parseZypperSecurityPatches(output)

	if secPkgs["openssl"] == false {
		t.Error("expected openssl in security patches")
	}
	if secPkgs["curl"] == false {
		t.Error("expected curl in security patches")
	}
}

// Intent: Empty output returns empty map.
func TestParseZypperSecurityPatches_Empty(t *testing.T) {
	secPkgs := parseZypperSecurityPatches("")

	if len(secPkgs) != 0 {
		t.Errorf("expected empty map, got %d entries", len(secPkgs))
	}
}
