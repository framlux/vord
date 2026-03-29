// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"fmt"
	"strconv"
	"strings"
	"unicode"
)

// maxDelayMinutes is the maximum allowed reboot delay.
const maxDelayMinutes = 1440

// defaultRebootReason is used when no reason is provided or the provided reason is invalid.
const defaultRebootReason = "Vord agent initiated reboot"

// RebootExecutor handles system reboot commands.
type RebootExecutor struct {
	runner CommandRunner
}

// Execute initiates a system reboot.
// Params:
//   - "delay_minutes": minutes to wait before reboot (default: 0 = immediate)
//   - "reason": optional reboot reason message
func (e *RebootExecutor) Execute(ctx context.Context, params map[string]string) Result {
	delayMinutes := parseDelayMinutes(params)
	reason := sanitizeReason(params["reason"])

	var timeArg string
	if delayMinutes == 0 {
		timeArg = "now"
	} else {
		timeArg = fmt.Sprintf("+%d", delayMinutes)
	}

	r := e.getRunner()
	output, err := r.Run(ctx, "shutdown", "-r", timeArg, reason)
	if err != nil {

		return Result{
			Success: false,
			Error:   fmt.Errorf("shutdown command failed: %w (output: %s)", err, string(output)),
		}
	}

	return Result{
		Success: true,
		Message: fmt.Sprintf("reboot scheduled in %d minutes", delayMinutes),
	}
}

func (e *RebootExecutor) getRunner() CommandRunner {
	if e.runner != nil {
		return e.runner
	}

	return defaultRunner{}
}

// parseDelayMinutes extracts and validates the delay_minutes parameter.
// Returns 0 if missing, non-numeric, or negative. Clamps to maxDelayMinutes.
func parseDelayMinutes(params map[string]string) int {
	d, ok := params["delay_minutes"]
	if ok == false {
		return 0
	}

	v, err := strconv.Atoi(d)
	if err != nil || v < 0 {
		return 0
	}

	if v > maxDelayMinutes {
		return maxDelayMinutes
	}

	return v
}

// sanitizeReason strips control characters, rejects flag-like strings, and limits length.
func sanitizeReason(reason string) string {
	if reason == "" {
		return defaultRebootReason
	}

	reason = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return -1
		}

		return r
	}, reason)

	if strings.HasPrefix(strings.TrimSpace(reason), "-") {
		return defaultRebootReason
	}

	if len(reason) > 200 {
		reason = reason[:200]
	}

	return reason
}
