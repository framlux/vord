// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/state"
)

// defaultAuthLogPaths are the files sshd logs to when there is no journal. Debian and Ubuntu use auth.log;
// RHEL, CentOS and SUSE use secure.
var defaultAuthLogPaths = []string{"/var/log/auth.log", "/var/log/secure"}

// SSHSessionsCollector monitors SSH connections via journalctl or auth.log.
type SSHSessionsCollector struct {
	hasJournalctl bool
	rs            *state.RuntimeState
	authLogPaths  []string
	// execCmd runs the journalctl read. Nil means runCmd; tests substitute a fake journal.
	execCmd func(ctx context.Context, name string, args ...string) ([]byte, error)
}

// NewSSHSessionsCollector creates a new SSHSessionsCollector.
func NewSSHSessionsCollector(rs *state.RuntimeState) *SSHSessionsCollector {
	_, err := exec.LookPath("journalctl")

	return &SSHSessionsCollector{hasJournalctl: err == nil, rs: rs, authLogPaths: defaultAuthLogPaths}
}

func (c *SSHSessionsCollector) Name() string { return "ssh_sessions" }
func (c *SSHSessionsCollector) DefaultInterval() time.Duration {
	return c.rs.TelemetryCollectFastInterval()
}

// sshd names an unknown account with an "invalid user " prefix ("illegal user " on
// older builds), so the username is not the word immediately after "for". Treating
// that prefix as optional is what lets both forms share one capture group.
const sshUnknownUserPrefix = `(?:invalid user |illegal user )?`

var (
	// Successful logins never carry the unknown-user prefix — sshd cannot accept an
	// account it does not have — so this pattern stays as-is.
	reAccepted     = regexp.MustCompile(`Accepted (\S+) for (\S+) from ([\d.:a-fA-F]+) port (\d+)`)
	reDisconnect   = regexp.MustCompile(`Disconnected from user (\S+) ([\d.:a-fA-F]+) port (\d+)`)
	reFailed       = regexp.MustCompile(`Failed (\S+) for ` + sshUnknownUserPrefix + `(\S+) from ([\d.:a-fA-F]+) port (\d+)`)
	reSessionOpen  = regexp.MustCompile(`session opened for user (\S+)`)
	reSessionClose = regexp.MustCompile(`session closed for user (\S+)`)
)

type sshJournalState struct {
	Cursor string `json:"cursor"`
}

// errJournalRead marks a failure to run journalctl, as opposed to a failure in the agent's own state.
var errJournalRead = errors.New("reading journal")

// authLogState is where collection stopped in the auth log: the byte offset already consumed, and the inode of
// the file it was consumed from so a rotation is not mistaken for a file that merely grew.
type authLogState struct {
	Offset int64  `json:"offset"`
	Inode  uint64 `json:"inode,omitempty"`
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

// ResetToNow moves the collection point to the present, so everything sshd logged before this call is skipped.
// It is what a resume does: without it the agent would send every SSH event from the pause and fire stale
// "new connection" and failed-login alerts. It picks the source the same way Collect does.
func (c *SSHSessionsCollector) ResetToNow(ctx context.Context, store *db.Store) error {
	if c.hasJournalctl {
		return c.resetJournal(ctx, store)
	}

	logPath := c.findAuthLog()
	if logPath == "" {
		return nil
	}

	fi, err := os.Stat(logPath)
	if err != nil {
		return fmt.Errorf("stat auth log: %w", err)
	}
	stateJSON, err := json.Marshal(authLogState{Offset: fi.Size(), Inode: fileInode(fi)})
	if err != nil {
		return fmt.Errorf("marshaling auth log state: %w", err)
	}
	stateStr := string(stateJSON)

	return store.SaveCollectorState(c.Name(), &stateStr)
}

// findAuthLog returns the first auth log file that exists, or "" when the host has none.
func (c *SSHSessionsCollector) findAuthLog() string {
	for _, p := range c.authLogPaths {
		if _, err := os.Stat(p); err == nil {
			return p
		}
	}

	return ""
}

// resetJournal advances the journal cursor past everything already logged by reading it the way Collect does and
// throwing the entries away. Dropping the cursor instead would make the next run re-read "the last five minutes",
// which is measured from when it runs: pause-era events after a long pause, pre-pause ones after a short one,
// all of them stamped as fresh by parseSSHLine.
func (c *SSHSessionsCollector) resetJournal(ctx context.Context, store *db.Store) error {
	newCursor, err := c.readJournal(ctx, store, false)
	if err != nil {
		if errors.Is(err, errJournalRead) {
			// The journal cannot be read now, so the old cursor would replay the whole pause. Dropping it
			// replays at most the recent window.
			if saveErr := store.SaveCollectorState(c.Name(), nil); saveErr != nil {
				return errors.Join(err, fmt.Errorf("dropping stale journal cursor: %w", saveErr))
			}
		}

		return err
	}

	return c.saveJournalCursor(store, newCursor)
}

func (c *SSHSessionsCollector) collectFromJournal(ctx context.Context, store *db.Store) error {
	newCursor, err := c.readJournal(ctx, store, true)
	if errors.Is(err, errJournalRead) {
		slog.Debug("journalctl failed, will retry next cycle", "error", err)

		return nil
	}
	if err != nil {
		return err
	}

	return c.saveJournalCursor(store, newCursor)
}

// readJournal runs the journal read behind both Collect and a reset: sshd entries after the saved cursor (or the
// last five minutes when there is none) with the cursor to continue from, which is "" when the journal had
// nothing to report. emit says whether the entries are stored as sessions or discarded.
func (c *SSHSessionsCollector) readJournal(ctx context.Context, store *db.Store, emit bool) (string, error) {
	// Load cursor from previous run.
	state, err := store.GetCollectorState(c.Name())
	if err != nil {
		return "", fmt.Errorf("getting collector state: %w", err)
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

	output, err := c.command(ctx, "journalctl", args...)
	if err != nil {
		return "", fmt.Errorf("%w: %w", errJournalRead, err)
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

		if emit == false {
			continue
		}

		sessions := parseSSHLine(line)
		for _, sess := range sessions {
			if err := storeSSHSession(store, &sess); err != nil {
				slog.Error("storing SSH session", "error", err)
			}
		}
	}

	return newCursor, nil
}

// saveJournalCursor records where the journal read stopped. An empty cursor means there was nothing new, so the
// saved one is still the right place to continue from.
func (c *SSHSessionsCollector) saveJournalCursor(store *db.Store, cursor string) error {
	if cursor == "" {
		return nil
	}

	stateJSON, _ := json.Marshal(sshJournalState{Cursor: cursor})
	stateStr := string(stateJSON)
	if err := store.SaveCollectorState(c.Name(), &stateStr); err != nil {
		return fmt.Errorf("saving collector state: %w", err)
	}

	return nil
}

// command runs an external command through the injected runner, or runCmd when there is none.
func (c *SSHSessionsCollector) command(ctx context.Context, name string, args ...string) ([]byte, error) {
	if c.execCmd != nil {
		return c.execCmd(ctx, name, args...)
	}

	return runCmd(ctx, name, args...)
}

func (c *SSHSessionsCollector) collectFromAuthLog(ctx context.Context, store *db.Store) error {
	logPath := c.findAuthLog()
	if logPath == "" {
		slog.Debug("no auth log file found")

		return nil
	}

	// Load line offset from previous run.
	state, err := store.GetCollectorState(c.Name())
	if err != nil {
		return fmt.Errorf("getting collector state: %w", err)
	}

	var offset int64
	var savedInode uint64
	if state != nil && state.StateJSON != nil {
		var als authLogState
		if err := json.Unmarshal([]byte(*state.StateJSON), &als); err != nil {
			slog.Debug("failed to unmarshal auth.log offset state", "error", err)
		} else {
			offset = als.Offset
			savedInode = als.Inode
		}
	}

	// Detect log rotation by checking both file size and inode number.
	// Size-only detection misses rotations where the new file grows past the old offset.
	if offset > 0 {
		fi, err := os.Stat(logPath)
		if err == nil {
			currentInode := fileInode(fi)
			rotated := fi.Size() < offset || (savedInode > 0 && currentInode != savedInode)
			if rotated {
				slog.Info("auth log rotated, resetting offset",
					"old_offset", offset, "file_size", fi.Size(),
					"old_inode", savedInode, "new_inode", currentInode,
				)
				offset = 0
			}
		}
	}

	// Use tail to read new lines from the auth log. tail counts bytes from 1, so the first unread byte after
	// consuming offset bytes is offset+1; starting at offset would re-read the last byte, and the offset would
	// creep past the end of an idle file until it was mistaken for a rotation and the whole log replayed.
	output, err := runCmd(ctx, "tail", "-c", fmt.Sprintf("+%d", offset+1), logPath)
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

	// Save offset and current inode for rotation detection.
	newOffset := offset + bytesRead
	var currentInode uint64
	if fi, err := os.Stat(logPath); err == nil {
		currentInode = fileInode(fi)
	}
	als := authLogState{Offset: newOffset, Inode: currentInode}
	stateJSON, _ := json.Marshal(als)
	stateStr := string(stateJSON)

	return store.SaveCollectorState(c.Name(), &stateStr)
}

// fileInode extracts the inode number from a FileInfo.
func fileInode(fi os.FileInfo) uint64 {
	if stat, ok := fi.Sys().(*syscall.Stat_t); ok {
		return stat.Ino
	}

	return 0
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
