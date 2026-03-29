// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"fmt"
	"os"
	"strconv"
	"syscall"
	"time"
)

// minProtectedPID is the minimum PID that can be killed to protect critical system processes.
const minProtectedPID = 100

// KillProcessExecutor handles process termination.
type KillProcessExecutor struct {
	signaler ProcessSignaler
}

// Execute terminates a process by PID.
// Params:
//   - "pid": process ID (required)
//   - "force": "true" to send SIGKILL immediately instead of SIGTERM first
func (e *KillProcessExecutor) Execute(ctx context.Context, params map[string]string) Result {
	pidStr, ok := params["pid"]
	if (ok == false) || pidStr == "" {

		return Result{
			Success: false,
			Error:   fmt.Errorf("pid parameter required"),
		}
	}

	pid, err := strconv.Atoi(pidStr)
	if err != nil {

		return Result{
			Success: false,
			Error:   fmt.Errorf("invalid pid: %w", err),
		}
	}

	if pid <= minProtectedPID {

		return Result{
			Success: false,
			Error:   fmt.Errorf("refusing to kill pid %d: protected range", pid),
		}
	}

	if pid == os.Getpid() {

		return Result{
			Success: false,
			Error:   fmt.Errorf("refusing to kill agent process (pid %d)", pid),
		}
	}

	sig := e.getSignaler()
	force := params["force"] == "true"

	if force {
		if err := sig.Signal(pid, syscall.SIGKILL); err != nil {

			return Result{
				Success: false,
				Error:   fmt.Errorf("SIGKILL failed for pid %d: %w", pid, err),
			}
		}

		return Result{
			Success: true,
			Message: fmt.Sprintf("sent SIGKILL to pid %d", pid),
		}
	}

	// Try SIGTERM first, then SIGKILL after timeout.
	if err := sig.Signal(pid, syscall.SIGTERM); err != nil {

		return Result{
			Success: false,
			Error:   fmt.Errorf("SIGTERM failed for pid %d: %w", pid, err),
		}
	}

	// Poll for process exit over 5 seconds before escalating to SIGKILL.
	exited := waitForExit(ctx, pid, 5*time.Second, sig)
	if exited {

		return Result{
			Success: true,
			Message: fmt.Sprintf("pid %d terminated after SIGTERM", pid),
		}
	}

	// Process still alive, escalate to SIGKILL.
	if err := sig.Signal(pid, syscall.SIGKILL); err != nil {

		return Result{
			Success: false,
			Error:   fmt.Errorf("SIGKILL escalation failed for pid %d: %w", pid, err),
		}
	}

	return Result{
		Success: true,
		Message: fmt.Sprintf("pid %d killed after SIGTERM+SIGKILL", pid),
	}
}

func (e *KillProcessExecutor) getSignaler() ProcessSignaler {
	if e.signaler != nil {
		return e.signaler
	}

	return defaultSignaler{}
}

// waitForExit polls for process termination over the given timeout.
// Returns true if the process exited, false if still running after timeout.
func waitForExit(ctx context.Context, pid int, timeout time.Duration, sig ProcessSignaler) bool {
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	tick := time.NewTicker(200 * time.Millisecond)
	defer tick.Stop()

	for {
		select {
		case <-ctx.Done():
			return false
		case <-deadline.C:
			return false
		case <-tick.C:
			if err := sig.Signal(pid, 0); err != nil {
				return true
			}
		}
	}
}
