// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os/exec"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
)

// PackagesCollector checks for available package updates.
type PackagesCollector struct {
	packageManager string // "apt", "dnf", "yum", "zypper", or ""
}

// NewPackagesCollector creates a new PackagesCollector.
func NewPackagesCollector() *PackagesCollector {
	var pm string
	if _, err := exec.LookPath("apt"); err == nil {
		pm = "apt"
	} else if _, err := exec.LookPath("dnf"); err == nil {
		pm = "dnf"
	} else if _, err := exec.LookPath("yum"); err == nil {
		pm = "yum"
	} else if _, err := exec.LookPath("zypper"); err == nil {
		pm = "zypper"
	}

	return &PackagesCollector{packageManager: pm}
}

func (c *PackagesCollector) Name() string              { return "package_updates" }
func (c *PackagesCollector) DefaultInterval() time.Duration { return 6 * time.Hour }

type packageUpdate struct {
	Name             string `json:"name"`
	CurrentVersion   string `json:"current_version"`
	AvailableVersion string `json:"available_version"`
	IsSecurityUpdate bool   `json:"is_security_update"`
}

type packagesPayload struct {
	PackageManager string          `json:"package_manager"`
	Updates        []packageUpdate `json:"updates"`
}

func (c *PackagesCollector) Collect(ctx context.Context, store *db.Store) error {
	if c.packageManager == "" {
		slog.Debug("no supported package manager found")

		return store.SaveCollectorState(c.Name(), nil)
	}

	var payload packagesPayload
	payload.PackageManager = c.packageManager

	switch c.packageManager {
	case "apt":
		payload.Updates = collectAptUpdates(ctx)
	case "dnf":
		payload.Updates = collectRpmUpdates(ctx, "dnf")
	case "yum":
		payload.Updates = collectRpmUpdates(ctx, "yum")
	case "zypper":
		payload.Updates = collectZypperUpdates(ctx)
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshaling package updates: %w", err)
	}

	if err := store.EnqueueTelemetry(id.NewV7(), db.TelemetryPackageUpdates, string(data)); err != nil {
		return fmt.Errorf("enqueuing package updates telemetry: %w", err)
	}

	return store.SaveCollectorState(c.Name(), nil)
}

func collectAptUpdates(ctx context.Context) []packageUpdate {
	out, err := runCmd(ctx, "apt", "list", "--upgradable")
	if err != nil {
		slog.Debug("apt list --upgradable failed", "error", err)

		return nil
	}

	return parseAptUpgradable(string(out))
}

// parseAptUpgradable parses the output of "apt list --upgradable" into package updates.
func parseAptUpgradable(output string) []packageUpdate {
	var updates []packageUpdate
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()
		// Format: "package/source version arch [upgradable from: current_version]"
		if strings.Contains(line, "[upgradable from:") == false {
			continue
		}

		parts := strings.SplitN(line, "/", 2)
		if len(parts) < 2 {
			continue
		}
		name := parts[0]

		rest := parts[1]
		fields := strings.Fields(rest)
		if len(fields) < 2 {
			continue
		}
		availVersion := fields[1]

		// Extract current version from "[upgradable from: X.Y.Z]"
		currentVersion := ""
		idx := strings.Index(rest, "[upgradable from: ")
		if idx >= 0 {
			sub := rest[idx+len("[upgradable from: "):]
			sub = strings.TrimSuffix(sub, "]")
			currentVersion = strings.TrimSpace(sub)
		}

		isSecurity := strings.Contains(rest, "-security")

		updates = append(updates, packageUpdate{
			Name:             name,
			CurrentVersion:   currentVersion,
			AvailableVersion: availVersion,
			IsSecurityUpdate: isSecurity,
		})
	}

	return updates
}

// collectRpmUpdates handles both dnf and yum package managers, which share
// the same output format for check-update and updateinfo commands.
func collectRpmUpdates(ctx context.Context, binary string) []packageUpdate {
	// Both dnf and yum return exit code 100 when updates are available.
	out, _ := runCmd(ctx, binary, "check-update", "--quiet")

	// Build set of security update package names.
	secOut, err := runCmd(ctx, binary, "updateinfo", "list", "sec", "--quiet")
	secPkgs := make(map[string]bool)
	if err == nil {
		secPkgs = parseRpmSecurityList(string(secOut))
	}

	return parseRpmCheckUpdate(string(out), secPkgs)
}

// parseRpmSecurityList parses the output of "dnf/yum updateinfo list sec" into a set
// of package names that have security updates available.
func parseRpmSecurityList(output string) map[string]bool {
	secPkgs := make(map[string]bool)
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		fields := strings.Fields(scanner.Text())
		if len(fields) >= 3 {
			pkgParts := strings.SplitN(fields[2], ".", 2)
			if len(pkgParts) > 0 {
				secPkgs[pkgParts[0]] = true
			}
		}
	}

	return secPkgs
}

// parseRpmCheckUpdate parses the output of "dnf/yum check-update" into package updates,
// cross-referencing against the provided security package set.
func parseRpmCheckUpdate(output string, secPkgs map[string]bool) []packageUpdate {
	var updates []packageUpdate
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}

		// Format: "package_name.arch  available_version  repo"
		nameParts := strings.SplitN(fields[0], ".", 2)
		if len(nameParts) == 0 {
			continue
		}
		name := nameParts[0]

		updates = append(updates, packageUpdate{
			Name:             name,
			AvailableVersion: fields[1],
			IsSecurityUpdate: secPkgs[name],
		})
	}

	return updates
}

func collectZypperUpdates(ctx context.Context) []packageUpdate {
	out, err := runCmd(ctx, "zypper", "--non-interactive", "--quiet", "list-updates")
	if err != nil {
		slog.Debug("zypper list-updates failed", "error", err)

		return nil
	}

	// Build set of security patch package names.
	secOut, err := runCmd(ctx, "zypper", "--non-interactive", "--quiet", "list-patches", "--category", "security")
	secPkgs := make(map[string]bool)
	if err == nil {
		secPkgs = parseZypperSecurityPatches(string(secOut))
	}

	return parseZypperUpdates(string(out), secPkgs)
}

// parseZypperSecurityPatches parses the output of "zypper list-patches --category security"
// into a set of package names that have security patches available.
func parseZypperSecurityPatches(output string) map[string]bool {
	secPkgs := make(map[string]bool)
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()
		if strings.HasPrefix(line, "--") || strings.TrimSpace(line) == "" {
			continue
		}
		cols := strings.Split(line, "|")
		if len(cols) >= 3 {
			secPkgs[strings.TrimSpace(cols[2])] = true
		}
	}

	return secPkgs
}

// parseZypperUpdates parses the output of "zypper list-updates" into package updates,
// cross-referencing against the provided security package set.
func parseZypperUpdates(output string, secPkgs map[string]bool) []packageUpdate {
	var updates []packageUpdate
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()

		// Skip header/separator lines.
		if strings.HasPrefix(line, "--") || strings.TrimSpace(line) == "" {
			continue
		}

		// Zypper tabular output: S | Repository | Name | Current Version | Available Version | Arch
		cols := strings.Split(line, "|")
		if len(cols) < 5 {
			continue
		}

		name := strings.TrimSpace(cols[2])
		if name == "" || name == "Name" {
			continue
		}

		currentVersion := strings.TrimSpace(cols[3])
		availableVersion := strings.TrimSpace(cols[4])

		updates = append(updates, packageUpdate{
			Name:             name,
			CurrentVersion:   currentVersion,
			AvailableVersion: availableVersion,
			IsSecurityUpdate: secPkgs[name],
		})
	}

	return updates
}