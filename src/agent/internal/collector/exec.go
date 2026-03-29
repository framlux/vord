// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"os/exec"
	"time"
)

// cmdTimeout is the default per-command execution timeout applied to external
// process invocations (ipmitool, smartctl, systemctl, apt, etc.).
const cmdTimeout = 30 * time.Second

// runCmd executes an external command with a per-command timeout derived from
// the parent context. This prevents any single command from blocking a
// collector indefinitely.
func runCmd(ctx context.Context, name string, args ...string) ([]byte, error) {
	cmdCtx, cancel := context.WithTimeout(ctx, cmdTimeout)
	defer cancel()

	return exec.CommandContext(cmdCtx, name, args...).Output()
}
