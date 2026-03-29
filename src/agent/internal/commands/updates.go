// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"fmt"
	"strings"
)

// CheckUpdateExecutor checks for available system updates.
type CheckUpdateExecutor struct {
	runner CommandRunner
	looker PathLooker
}

// Execute checks for package updates.
func (e *CheckUpdateExecutor) Execute(ctx context.Context, params map[string]string) Result {
	r := e.getRunner()
	l := e.getLooker()

	if _, err := l.LookPath("apt"); err == nil {
		out, err := r.Run(ctx, "apt", "list", "--upgradable")
		if err != nil {

			return Result{Success: false, Error: fmt.Errorf("apt list failed: %w", err)}
		}
		lines := strings.Split(strings.TrimSpace(string(out)), "\n")
		// First line is "Listing..." header.
		count := 0
		if len(lines) > 1 {
			count = len(lines) - 1
		}

		return Result{
			Success: true,
			Message: fmt.Sprintf("%d packages upgradable (apt)", count),
		}
	}

	if _, err := l.LookPath("dnf"); err == nil {
		out, _ := r.Run(ctx, "dnf", "check-update", "--quiet")
		lines := strings.Split(strings.TrimSpace(string(out)), "\n")
		count := 0
		for _, line := range lines {
			if strings.TrimSpace(line) != "" {
				count++
			}
		}

		return Result{
			Success: true,
			Message: fmt.Sprintf("%d packages updatable (dnf)", count),
		}
	}

	return Result{Success: false, Error: fmt.Errorf("no supported package manager found")}
}

func (e *CheckUpdateExecutor) getRunner() CommandRunner {
	if e.runner != nil {
		return e.runner
	}

	return defaultRunner{}
}

func (e *CheckUpdateExecutor) getLooker() PathLooker {
	if e.looker != nil {
		return e.looker
	}

	return defaultLooker{}
}

// InstallUpdateExecutor installs available system updates.
type InstallUpdateExecutor struct {
	runner CommandRunner
	looker PathLooker
}

// Execute installs available package updates.
func (e *InstallUpdateExecutor) Execute(ctx context.Context, params map[string]string) Result {
	r := e.getRunner()
	l := e.getLooker()

	if _, err := l.LookPath("apt"); err == nil {
		// Update package lists first.
		if out, err := r.Run(ctx, "apt", "update", "-qq"); err != nil {

			return Result{Success: false, Error: fmt.Errorf("apt update failed: %w (output: %s)", err, string(out))}
		}

		out, err := r.Run(ctx, "apt", "upgrade", "-y", "-qq")
		if err != nil {

			return Result{Success: false, Error: fmt.Errorf("apt upgrade failed: %w (output: %s)", err, string(out))}
		}

		return Result{
			Success: true,
			Message: fmt.Sprintf("apt upgrade completed: %s", strings.TrimSpace(string(out))),
		}
	}

	if _, err := l.LookPath("dnf"); err == nil {
		out, err := r.Run(ctx, "dnf", "update", "-y", "--quiet")
		if err != nil {

			return Result{Success: false, Error: fmt.Errorf("dnf update failed: %w (output: %s)", err, string(out))}
		}

		return Result{
			Success: true,
			Message: fmt.Sprintf("dnf update completed: %s", strings.TrimSpace(string(out))),
		}
	}

	return Result{Success: false, Error: fmt.Errorf("no supported package manager found")}
}

func (e *InstallUpdateExecutor) getRunner() CommandRunner {
	if e.runner != nil {
		return e.runner
	}

	return defaultRunner{}
}

func (e *InstallUpdateExecutor) getLooker() PathLooker {
	if e.looker != nil {
		return e.looker
	}

	return defaultLooker{}
}
