// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"testing"

	"github.com/framlux/vord/internal/db"
)

// Intent: "Accepted publickey" log line → connect action with user, IP, port, auth method.
func TestParseSSHLine_AcceptedLogin(t *testing.T) {
	line := "Jun 15 10:30:00 server sshd[1234]: Accepted publickey for root from 10.0.1.50 port 54321 ssh2"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}

	r := results[0]
	if r.Action != "connect" {
		t.Errorf("expected Action=connect, got %q", r.Action)
	}
	if r.User != "root" {
		t.Errorf("expected User=root, got %q", r.User)
	}
	if r.SourceIP != "10.0.1.50" {
		t.Errorf("expected SourceIP=10.0.1.50, got %q", r.SourceIP)
	}
	if r.SourcePort != 54321 {
		t.Errorf("expected SourcePort=54321, got %d", r.SourcePort)
	}
	if r.AuthMethod != "publickey" {
		t.Errorf("expected AuthMethod=publickey, got %q", r.AuthMethod)
	}
	if r.Timestamp == "" {
		t.Error("expected non-empty Timestamp")
	}
}

// Intent: "Accepted password" log line → connect action with password auth method.
func TestParseSSHLine_AcceptedPassword(t *testing.T) {
	line := "Accepted password for admin from 192.168.1.100 port 22222 ssh2"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}
	if results[0].AuthMethod != "password" {
		t.Errorf("expected AuthMethod=password, got %q", results[0].AuthMethod)
	}
	if results[0].User != "admin" {
		t.Errorf("expected User=admin, got %q", results[0].User)
	}
}

// Intent: "Disconnected from user" log line → disconnect action.
func TestParseSSHLine_Disconnect(t *testing.T) {
	line := "Jun 15 10:45:00 server sshd[1234]: Disconnected from user deploy 10.0.1.50 port 54321"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}

	r := results[0]
	if r.Action != "disconnect" {
		t.Errorf("expected Action=disconnect, got %q", r.Action)
	}
	if r.User != "deploy" {
		t.Errorf("expected User=deploy, got %q", r.User)
	}
	if r.SourceIP != "10.0.1.50" {
		t.Errorf("expected SourceIP=10.0.1.50, got %q", r.SourceIP)
	}
	if r.SourcePort != 54321 {
		t.Errorf("expected SourcePort=54321, got %d", r.SourcePort)
	}
}

// Intent: "Failed password" log line → failed action with auth method.
func TestParseSSHLine_FailedLogin(t *testing.T) {
	line := "Jun 15 10:35:00 server sshd[1234]: Failed password for root from 203.0.113.5 port 44444 ssh2"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}

	r := results[0]
	if r.Action != "failed" {
		t.Errorf("expected Action=failed, got %q", r.Action)
	}
	if r.User != "root" {
		t.Errorf("expected User=root, got %q", r.User)
	}
	if r.AuthMethod != "password" {
		t.Errorf("expected AuthMethod=password, got %q", r.AuthMethod)
	}
	if r.SourceIP != "203.0.113.5" {
		t.Errorf("expected SourceIP=203.0.113.5, got %q", r.SourceIP)
	}
}

// Intent: "session opened" log line → connect action with user only.
func TestParseSSHLine_SessionOpened(t *testing.T) {
	line := "Jun 15 10:30:01 server sshd[1234]: pam_unix(sshd:session): session opened for user bob"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}

	r := results[0]
	if r.Action != "connect" {
		t.Errorf("expected Action=connect, got %q", r.Action)
	}
	if r.User != "bob" {
		t.Errorf("expected User=bob, got %q", r.User)
	}
	if r.SourceIP != "" {
		t.Errorf("expected empty SourceIP for session open, got %q", r.SourceIP)
	}
}

// Intent: "session closed" log line → disconnect action with user only.
func TestParseSSHLine_SessionClosed(t *testing.T) {
	line := "Jun 15 10:45:01 server sshd[1234]: pam_unix(sshd:session): session closed for user bob"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}

	r := results[0]
	if r.Action != "disconnect" {
		t.Errorf("expected Action=disconnect, got %q", r.Action)
	}
	if r.User != "bob" {
		t.Errorf("expected User=bob, got %q", r.User)
	}
}

// Intent: Unrelated log line produces no results.
func TestParseSSHLine_UnrelatedLine(t *testing.T) {
	lines := []string{
		"Jun 15 10:30:00 server kernel: something happened",
		"",
		"Connection reset by peer",
		"sshd[1234]: Server listening on 0.0.0.0 port 22",
	}

	for _, line := range lines {
		results := parseSSHLine(line)
		if len(results) != 0 {
			t.Errorf("expected 0 results for line %q, got %d", line, len(results))
		}
	}
}

// Intent: IPv6 source address is parsed correctly.
func TestParseSSHLine_IPv6Address(t *testing.T) {
	line := "Accepted publickey for user1 from fd00::1 port 12345 ssh2"
	results := parseSSHLine(line)

	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}
	if results[0].SourceIP != "fd00::1" {
		t.Errorf("expected SourceIP=fd00::1, got %q", results[0].SourceIP)
	}
}

// Intent: every shape of sshd auth line the alerting path depends on is parsed into the
// right action with its fields intact, and lines that are not auth events are dropped.
// Brute-force traffic is dominated by unknown accounts, so the "invalid user" forms must
// produce failed events rather than being silently discarded.
func TestParseSSHLine_AuthLineShapes(t *testing.T) {
	cases := []struct {
		name       string
		line       string
		action     string
		user       string
		sourceIP   string
		sourcePort int
		authMethod string
	}{
		{
			name:       "valid user failure",
			line:       "Jun 15 10:35:00 server sshd[1234]: Failed password for root from 203.0.113.5 port 44444 ssh2",
			action:     "failed",
			user:       "root",
			sourceIP:   "203.0.113.5",
			sourcePort: 44444,
			authMethod: "password",
		},
		{
			name:       "invalid user failure",
			line:       "Jun 15 10:35:01 server sshd[1234]: Failed password for invalid user bob from 203.0.113.6 port 44445 ssh2",
			action:     "failed",
			user:       "bob",
			sourceIP:   "203.0.113.6",
			sourcePort: 44445,
			authMethod: "password",
		},
		{
			name:       "illegal user failure from older sshd",
			line:       "Jun 15 10:35:02 server sshd[1234]: Failed password for illegal user oracle from 203.0.113.7 port 44446 ssh2",
			action:     "failed",
			user:       "oracle",
			sourceIP:   "203.0.113.7",
			sourcePort: 44446,
			authMethod: "password",
		},
		{
			name:       "invalid user none method probe",
			line:       "Jun 15 10:35:03 server sshd[1234]: Failed none for invalid user admin from 203.0.113.8 port 44447 ssh2",
			action:     "failed",
			user:       "admin",
			sourceIP:   "203.0.113.8",
			sourcePort: 44447,
			authMethod: "none",
		},
		{
			name:       "account literally named invalid is not mistaken for the prefix",
			line:       "Jun 15 10:35:04 server sshd[1234]: Failed password for invalid from 203.0.113.9 port 44448 ssh2",
			action:     "failed",
			user:       "invalid",
			sourceIP:   "203.0.113.9",
			sourcePort: 44448,
			authMethod: "password",
		},
		{
			name:       "invalid user failure over IPv6",
			line:       "Jun 15 10:35:05 server sshd[1234]: Failed publickey for invalid user git from 2001:db8::dead:beef port 44449 ssh2",
			action:     "failed",
			user:       "git",
			sourceIP:   "2001:db8::dead:beef",
			sourcePort: 44449,
			authMethod: "publickey",
		},
		{
			name:       "successful connect is still a connect",
			line:       "Jun 15 10:30:00 server sshd[1234]: Accepted publickey for deploy from 10.0.1.50 port 54321 ssh2",
			action:     "connect",
			user:       "deploy",
			sourceIP:   "10.0.1.50",
			sourcePort: 54321,
			authMethod: "publickey",
		},
		{
			name:       "disconnect is still a disconnect",
			line:       "Jun 15 10:45:00 server sshd[1234]: Disconnected from user deploy 10.0.1.50 port 54321",
			action:     "disconnect",
			user:       "deploy",
			sourceIP:   "10.0.1.50",
			sourcePort: 54321,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			results := parseSSHLine(tc.line)
			if len(results) != 1 {
				t.Fatalf("expected 1 result, got %d", len(results))
			}

			r := results[0]
			if r.Action != tc.action {
				t.Errorf("expected Action=%q, got %q", tc.action, r.Action)
			}
			if r.User != tc.user {
				t.Errorf("expected User=%q, got %q", tc.user, r.User)
			}
			if r.SourceIP != tc.sourceIP {
				t.Errorf("expected SourceIP=%q, got %q", tc.sourceIP, r.SourceIP)
			}
			if r.SourcePort != tc.sourcePort {
				t.Errorf("expected SourcePort=%d, got %d", tc.sourcePort, r.SourcePort)
			}
			if r.AuthMethod != tc.authMethod {
				t.Errorf("expected AuthMethod=%q, got %q", tc.authMethod, r.AuthMethod)
			}
			if r.Timestamp == "" {
				t.Error("expected non-empty Timestamp")
			}
		})
	}
}

// Intent: truncated or garbled auth lines are dropped rather than parsed into a
// half-populated event, and parsing never panics on them.
func TestParseSSHLine_MalformedAuthLinesDropped(t *testing.T) {
	cases := []struct {
		name string
		line string
	}{
		{name: "non-numeric address", line: "sshd[1234]: Failed password for invalid user bob from notanip port 22 ssh2"},
		{name: "non-numeric port", line: "sshd[1234]: Failed password for invalid user bob from 203.0.113.5 port xyz ssh2"},
		{name: "truncated before source", line: "sshd[1234]: Failed password for invalid user bob"},
		{name: "prefix with no username", line: "sshd[1234]: Failed password for invalid user from 203.0.113.5 port 22 ssh2"},
		{name: "max attempts truncated", line: "sshd[1234]: error: maximum authentication attempts exceeded for"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			results := parseSSHLine(tc.line)
			if len(results) != 0 {
				t.Errorf("expected 0 results for %q, got %d (%+v)", tc.line, len(results), results)
			}
		})
	}
}

// Intent: sshd's "maximum authentication attempts exceeded" line reports that a connection
// was torn down, not that another credential was tried. It is not collected, because every
// attempt that exhausted the limit already arrived as its own "Failed" line.
func TestParseSSHLine_MaxAuthTriesSummaryNotCollected(t *testing.T) {
	cases := []struct {
		name string
		line string
	}{
		{name: "invalid user", line: "Jun 15 10:35:06 server sshd[1234]: error: maximum authentication attempts exceeded for invalid user bob from 203.0.113.10 port 44450 ssh2 [preauth]"},
		{name: "valid user", line: "Jun 15 10:35:07 server sshd[1234]: error: maximum authentication attempts exceeded for root from 203.0.113.11 port 44451 ssh2 [preauth]"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			results := parseSSHLine(tc.line)
			if len(results) != 0 {
				t.Errorf("expected 0 results for %q, got %d (%+v)", tc.line, len(results), results)
			}
		})
	}
}

// Intent: a connection that burns every attempt contributes exactly one failed event per
// attempt. sshd emits its summary line in addition to the per-attempt lines, so collecting
// both would report more failures than the host saw and shift the threshold a tenant tunes
// against — six real attempts must count as six, not seven.
func TestParseSSHLine_ExhaustedConnectionCountsEachAttemptOnce(t *testing.T) {
	lines := []string{
		"Jun 15 10:35:00 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:01 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:02 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:03 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:04 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:05 server sshd[1234]: Failed password for invalid user bob from 203.0.113.10 port 44450 ssh2",
		"Jun 15 10:35:06 server sshd[1234]: error: maximum authentication attempts exceeded for invalid user bob from 203.0.113.10 port 44450 ssh2 [preauth]",
	}

	failed := 0
	for _, line := range lines {
		for _, r := range parseSSHLine(line) {
			if r.Action == "failed" {
				failed++
			}
		}
	}

	if failed != 6 {
		t.Errorf("expected 6 failed events for a connection that exhausted MaxAuthTries=6, got %d", failed)
	}
}

// Intent: storeSSHSession inserts SSH session and enqueues telemetry in one operation.
func TestStoreSSHSession_InsertsAndEnqueues(t *testing.T) {
	store := newTestStore(t)

	payload := &sshSessionPayload{
		User:       "testuser",
		SourceIP:   "10.0.0.1",
		SourcePort: 22222,
		Action:     "connect",
		AuthMethod: "publickey",
		Timestamp:  "2026-01-01T00:00:00Z",
	}

	err := storeSSHSession(store, payload)
	if err != nil {
		t.Fatalf("storeSSHSession: %v", err)
	}

	// Verify telemetry was enqueued.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetrySSHSession {
		t.Errorf("expected type %d, got %d", db.TelemetrySSHSession, items[0].ItemType)
	}
}

// Intent: storeSSHSession with minimal payload (no optional fields) still succeeds.
func TestStoreSSHSession_MinimalPayload(t *testing.T) {
	store := newTestStore(t)

	payload := &sshSessionPayload{
		User:      "root",
		Action:    "connect",
		Timestamp: "2026-01-01T00:00:00Z",
	}

	err := storeSSHSession(store, payload)
	if err != nil {
		t.Fatalf("storeSSHSession: %v", err)
	}

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
}
