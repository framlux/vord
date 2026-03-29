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
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
)

// SSHSessionsCollector monitors SSH connections via journalctl or auth.log.
type SSHSessionsCollector struct {
	hasJournalctl bool
}

// NewSSHSessionsCollector creates a new SSHSessionsCollector.
func NewSSHSessionsCollector() *SSHSessionsCollector {
	_, err := exec.LookPath("journalctl")

	return &SSHSessionsCollector{hasJournalctl: err == nil}
}

func (c *SSHSessionsCollector) Name() string              { return "ssh_sessions" }
func (c *SSHSessionsCollector) DefaultInterval() time.Duration { return 10 * time.Second }

var (
	reAccepted    = regexp.MustCompile(`Accepted (\S+) for (\S+) from ([\d.:a-fA-F]+) port (\d+)`)
	reDisconnect  = regexp.MustCompile(`Disconnected from user (\S+) ([\d.:a-fA-F]+) port (\d+)`)
	reFailed      = regexp.MustCompile(`Failed (\S+) for (\S+) from ([\d.:a-fA-F]+) port (\d+)`)
	reSessionOpen = regexp.MustCompile(`session opened for user (\S+)`)
	reSessionClose = regexp.MustCompile(`session closed for user (\S+)`)
)

type sshJournalState struct {
	Cursor string `json:"cursor"`
}

type sshSessionPayload struct {
	SessionID  string `json:"session_id,omitempty"`
	User       string `json:"user"`
	SourceIP   string `json:"source_ip,omitempty"`
	SourcePort int    `json:"source_port,omitempty"`
	Action     string `json:"action"`
	AuthMethod string `json:"auth_method,omitempty"`
	Timestamp  string `json:"timestamp"`
}

func (c *SSHSessionsCollector) Collect(ctx context.Context, store *db.Store) error {
	if c.hasJournalctl {
		return c.collectFromJournal(ctx, store)
	}

	return c.collectFromAuthLog(ctx, store)
}

func (c *SSHSessionsCollector) collectFromJournal(ctx context.Context, store *db.Store) error {
	// Load cursor from previous run.
	state, err := store.GetCollectorState(c.Name())
	if err != nil {
		return fmt.Errorf("getting collector state: %w", err)
	}

	args := []string{"-u", "sshd", "--output=cat", "--no-pager"}

	if state != nil && state.StateJSON != nil {
		var jState sshJournalState
		if err := json.Unmarshal([]byte(*state.StateJSON), &jState); err != nil {
			slog.Debug("failed to unmarshal journal cursor state", "error", err)
		} else if jState.Cursor != "" {
			args = append(args, "--after-cursor="+jState.Cursor)
		}
	} else {
		// First run: only look at recent entries.
		args = append(args, "--since=-5m")
	}
	args = append(args, "--show-cursor")

	output, err := runCmd(ctx, "journalctl", args...)
	if err != nil {
		slog.Debug("journalctl failed, will retry next cycle", "error", err)
		return nil
	}

	lines := strings.Split(string(output), "\n")
	var newCursor string

	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}

		// journalctl --show-cursor outputs "-- cursor: s=..." at the end.
		if strings.HasPrefix(line, "-- cursor: ") {
			newCursor = strings.TrimPrefix(line, "-- cursor: ")
			continue
		}

		sessions := parseSSHLine(line)
		for _, sess := range sessions {
			if err := storeSSHSession(store, &sess); err != nil {
				slog.Error("storing SSH session", "error", err)
			}
		}
	}

	// Save cursor for next run.
	if newCursor != "" {
		jState := sshJournalState{Cursor: newCursor}
		stateJSON, _ := json.Marshal(jState)
		stateStr := string(stateJSON)
		if err := store.SaveCollectorState(c.Name(), &stateStr); err != nil {
			return fmt.Errorf("saving collector state: %w", err)
		}
	}

	return nil
}

func (c *SSHSessionsCollector) collectFromAuthLog(ctx context.Context, store *db.Store) error {
	// Find the auth log file — Debian/Ubuntu use auth.log, RHEL/CentOS/SUSE use secure.
	logPaths := []string{"/var/log/auth.log", "/var/log/secure"}
	var logPath string
	for _, p := range logPaths {
		if _, err := os.Stat(p); err == nil {
			logPath = p
			break
		}
	}
	if logPath == "" {
		slog.Debug("no auth log file found")

		return nil
	}

	// Load line offset from previous run.
	state, err := store.GetCollectorState(c.Name())
	if err != nil {
		return fmt.Errorf("getting collector state: %w", err)
	}

	type authLogState struct {
		Offset int64 `json:"offset"`
	}

	var offset int64
	if state != nil && state.StateJSON != nil {
		var als authLogState
		if err := json.Unmarshal([]byte(*state.StateJSON), &als); err != nil {
			slog.Debug("failed to unmarshal auth.log offset state", "error", err)
		} else {
			offset = als.Offset
		}
	}

	// Reset offset if the file has been rotated (new file is shorter than saved offset).
	if offset > 0 {
		fi, err := os.Stat(logPath)
		if err == nil && fi.Size() < offset {
			slog.Info("auth log rotated, resetting offset", "old_offset", offset, "file_size", fi.Size())
			offset = 0
		}
	}

	// Use tail to read new lines from the auth log.
	output, err := runCmd(ctx, "tail", "-n", "+0", "-c", fmt.Sprintf("+%d", offset), logPath)
	if err != nil {
		slog.Debug("reading auth log failed", "error", err, "path", logPath)
		return nil
	}

	scanner := bufio.NewScanner(strings.NewReader(string(output)))
	var bytesRead int64
	for scanner.Scan() {
		line := scanner.Text()
		bytesRead += int64(len(line)) + 1 // +1 for newline

		if strings.Contains(line, "sshd") == false {
			continue
		}

		sessions := parseSSHLine(line)
		for _, sess := range sessions {
			if err := storeSSHSession(store, &sess); err != nil {
				slog.Error("storing SSH session", "error", err)
			}
		}
	}

	// Save offset.
	newOffset := offset + bytesRead
	als := authLogState{Offset: newOffset}
	stateJSON, _ := json.Marshal(als)
	stateStr := string(stateJSON)
	return store.SaveCollectorState(c.Name(), &stateStr)
}

func parseSSHLine(line string) []sshSessionPayload {
	now := time.Now().UTC().Format(time.RFC3339)
	var results []sshSessionPayload

	if m := reAccepted.FindStringSubmatch(line); m != nil {
		port, _ := strconv.Atoi(m[4])
		results = append(results, sshSessionPayload{
			User:       m[2],
			SourceIP:   m[3],
			SourcePort: port,
			Action:     "connect",
			AuthMethod: m[1],
			Timestamp:  now,
		})
	} else if m := reDisconnect.FindStringSubmatch(line); m != nil {
		port, _ := strconv.Atoi(m[3])
		results = append(results, sshSessionPayload{
			User:       m[1],
			SourceIP:   m[2],
			SourcePort: port,
			Action:     "disconnect",
			Timestamp:  now,
		})
	} else if m := reFailed.FindStringSubmatch(line); m != nil {
		port, _ := strconv.Atoi(m[4])
		results = append(results, sshSessionPayload{
			User:       m[2],
			SourceIP:   m[3],
			SourcePort: port,
			Action:     "failed",
			AuthMethod: m[1],
			Timestamp:  now,
		})
	} else if m := reSessionOpen.FindStringSubmatch(line); m != nil {
		results = append(results, sshSessionPayload{
			User:      m[1],
			Action:    "connect",
			Timestamp: now,
		})
	} else if m := reSessionClose.FindStringSubmatch(line); m != nil {
		results = append(results, sshSessionPayload{
			User:      m[1],
			Action:    "disconnect",
			Timestamp: now,
		})
	}

	return results
}

// storeSSHSession inserts an SSH session, enqueues it as telemetry, and marks
// it queued — all within a single transaction to prevent inconsistent state.
func storeSSHSession(store *db.Store, payload *sshSessionPayload) error {
	sessionUUID := id.NewV7()

	var sessionID *string
	var sourceIP *string
	var sourcePort *int
	var authMethod *string

	if payload.SessionID != "" {
		sessionID = &payload.SessionID
	}
	if payload.SourceIP != "" {
		sourceIP = &payload.SourceIP
	}
	if payload.SourcePort != 0 {
		sourcePort = &payload.SourcePort
	}
	if payload.AuthMethod != "" {
		authMethod = &payload.AuthMethod
	}

	session := &db.SSHSession{
		ID:         sessionUUID,
		SessionID:  sessionID,
		User:       payload.User,
		SourceIP:   sourceIP,
		SourcePort: sourcePort,
		Action:     payload.Action,
		AuthMethod: authMethod,
		Timestamp:  payload.Timestamp,
		Queued:     0,
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}

	return store.InsertSSHSessionAndEnqueue(session, id.NewV7(), string(data))
}