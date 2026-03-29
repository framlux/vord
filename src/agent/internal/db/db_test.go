// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package db

import (
	"os"
	"runtime"
	"testing"
)

// Intent: :memory: opens without error and returns a usable connection.
func TestOpen_InMemory(t *testing.T) {
	conn, err := Open(":memory:")
	if err != nil {
		t.Fatalf("Open(:memory:) error: %v", err)
	}
	defer conn.Close()

	// Verify the connection is usable.
	var result int
	if err := conn.QueryRow("SELECT 1").Scan(&result); err != nil {
		t.Fatalf("query on in-memory db failed: %v", err)
	}
	if result != 1 {
		t.Errorf("expected 1, got %d", result)
	}
}

// Intent: DB file must be created with 0600 permissions (security — API key stored here).
// This test only runs on Linux because the agent is Linux-only and sql.Open with
// modernc.org/sqlite creates the file eagerly only on Linux.
func TestOpen_FilePermissions(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("skipping on-disk permission test on non-Linux platform")
	}

	dir := t.TempDir()
	dbPath := dir + "/test.db"

	conn, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open(%s) error: %v", dbPath, err)
	}
	defer conn.Close()

	// Verify the database file has owner-only permissions (0600).
	info, err := os.Stat(dbPath)
	if err != nil {
		t.Fatalf("Stat(%s): %v", dbPath, err)
	}
	perm := info.Mode().Perm()
	if perm != 0600 {
		t.Errorf("expected file permissions 0600, got %04o", perm)
	}
}

// Intent: Database uses WAL journal mode for crash safety and concurrent reads.
func TestOpen_WALMode(t *testing.T) {
	conn, err := Open(":memory:")
	if err != nil {
		t.Fatalf("Open(:memory:) error: %v", err)
	}
	defer conn.Close()

	var journalMode string
	if err := conn.QueryRow("PRAGMA journal_mode").Scan(&journalMode); err != nil {
		t.Fatalf("PRAGMA journal_mode: %v", err)
	}

	// In-memory databases may use "memory" journal mode instead of WAL.
	// For on-disk databases, WAL is expected.
	if journalMode != "wal" && journalMode != "memory" {
		t.Errorf("expected journal_mode 'wal' or 'memory', got %q", journalMode)
	}
}

// Intent: Database file uses WAL journal mode (on-disk test).
// Only runs on Linux where the agent actually deploys.
func TestOpen_WALMode_OnDisk(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("skipping on-disk WAL test on non-Linux platform")
	}

	dir := t.TempDir()
	dbPath := dir + "/wal-test.db"

	conn, err := Open(dbPath)
	if err != nil {
		t.Fatalf("Open(%s) error: %v", dbPath, err)
	}
	defer conn.Close()

	var journalMode string
	if err := conn.QueryRow("PRAGMA journal_mode").Scan(&journalMode); err != nil {
		t.Fatalf("PRAGMA journal_mode: %v", err)
	}

	if journalMode != "wal" {
		t.Errorf("expected journal_mode 'wal', got %q", journalMode)
	}
}

// Intent: Newly opened DB passes integrity check.
func TestOpen_IntegrityCheck(t *testing.T) {
	conn, err := Open(":memory:")
	if err != nil {
		t.Fatalf("Open(:memory:) error: %v", err)
	}
	defer conn.Close()

	var result string
	if err := conn.QueryRow("PRAGMA integrity_check").Scan(&result); err != nil {
		t.Fatalf("PRAGMA integrity_check: %v", err)
	}
	if result != "ok" {
		t.Errorf("expected integrity_check 'ok', got %q", result)
	}
}

// Intent: Required tables exist after Open() — migrations ran successfully.
func TestOpen_MigrationsRun(t *testing.T) {
	conn, err := Open(":memory:")
	if err != nil {
		t.Fatalf("Open(:memory:) error: %v", err)
	}
	defer conn.Close()

	requiredTables := []string{
		"agent_config",
		"telemetry_queue",
		"server_config",
		"ssh_sessions",
		"collector_state",
	}

	for _, table := range requiredTables {
		var name string
		err := conn.QueryRow(
			"SELECT name FROM sqlite_master WHERE type='table' AND name=?", table,
		).Scan(&name)
		if err != nil {
			t.Errorf("table %q not found after migration: %v", table, err)
		}
	}
}

// Intent: Required indexes exist after Open().
func TestOpen_IndexesCreated(t *testing.T) {
	conn, err := Open(":memory:")
	if err != nil {
		t.Fatalf("Open(:memory:) error: %v", err)
	}
	defer conn.Close()

	requiredIndexes := []string{
		"idx_telemetry_queue_status",
		"idx_telemetry_queue_type",
		"idx_ssh_sessions_queued",
	}

	for _, idx := range requiredIndexes {
		var name string
		err := conn.QueryRow(
			"SELECT name FROM sqlite_master WHERE type='index' AND name=?", idx,
		).Scan(&name)
		if err != nil {
			t.Errorf("index %q not found after migration: %v", idx, err)
		}
	}
}
