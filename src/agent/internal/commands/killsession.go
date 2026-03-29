// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"fmt"
	"regexp"
)

var validSessionID = regexp.MustCompile(`^[a-zA-Z0-9_-]+$`)

// KillSessionExecutor handles login session termination.
type KillSessionExecutor struct {
	runner CommandRunner
}

// Execute terminates a login session.
// Params:
//   - "session_id": loginctl session identifier (required)
func (e *KillSessionExecutor) Execute(ctx context.Context, params map[string]string) Result {
	sessionID, ok := params["session_id"]
	if (ok == false) || sessionID == "" {

		return Result{
			Success: false,
			Error:   fmt.Errorf("session_id parameter required"),
		}
	}

	if validSessionID.MatchString(sessionID) == false {

		return Result{
			Success: false,
			Error:   fmt.Errorf("invalid session_id format"),
		}
	}

	r := e.getRunner()
	output, err := r.Run(ctx, "loginctl", "terminate-session", sessionID)
	if err != nil {

		return Result{
			Success: false,
			Error:   fmt.Errorf("loginctl terminate-session failed: %w (output: %s)", err, string(output)),
		}
	}

	return Result{
		Success: true,
		Message: fmt.Sprintf("terminated session %s", sessionID),
	}
}

func (e *KillSessionExecutor) getRunner() CommandRunner {
	if e.runner != nil {
		return e.runner
	}

	return defaultRunner{}
}
