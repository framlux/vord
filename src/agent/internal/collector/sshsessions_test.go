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
