// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package db provides SQLite storage for agent configuration, telemetry queue, and collector state.
package db

import (
	"database/sql"
	"fmt"
	"log/slog"
	"os"
	"syscall"

	_ "modernc.org/sqlite"
)

// Open opens (or creates) the SQLite database at the given path and runs migrations.
func Open(dbPath string) (*sql.DB, error) {
	// Only set file permissions for on-disk databases; :memory: has no file.
	if dbPath != ":memory:" {
		oldUmask := syscall.Umask(0077)
		defer syscall.Umask(oldUmask)
	}

	dsn := fmt.Sprintf("file:%s?_journal_mode=WAL&_busy_timeout=5000&_synchronous=NORMAL", dbPath)
	conn, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("opening database: %w", err)
	}

	conn.SetMaxOpenConns(1)

	// Restrict database file to owner-only access (0600). The agent runs as root,
	// so this ensures the API key stored in agent_config is only readable by root.
	if dbPath != ":memory:" {
		if err := os.Chmod(dbPath, 0600); err != nil {
			conn.Close()

			return nil, fmt.Errorf("setting database file permissions: %w", err)
		}
	}

	if err := migrate(conn); err != nil {
		conn.Close()

		return nil, fmt.Errorf("running migrations: %w", err)
	}

	// Verify database integrity after migrations (catches WAL recovery issues).
	var result string
	if err := conn.QueryRow("PRAGMA integrity_check").Scan(&result); err != nil {
		conn.Close()

		return nil, fmt.Errorf("integrity check failed: %w", err)
	}
	if result != "ok" {
		conn.Close()

		return nil, fmt.Errorf("database integrity check failed: %s", result)
	}

	return conn, nil
}

func migrate(conn *sql.DB) error {
	slog.Info("running database migrations")

	statements := []string{
		`CREATE TABLE IF NOT EXISTS agent_config (
			key TEXT PRIMARY KEY,
			value TEXT NOT NULL,
			updated_at TEXT NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS telemetry_queue (
			id TEXT PRIMARY KEY,
			item_type INTEGER NOT NULL,
			payload TEXT NOT NULL,
			status INTEGER NOT NULL DEFAULT 0,
			created_at TEXT NOT NULL,
			updated_at TEXT NOT NULL
		)`,
		`CREATE INDEX IF NOT EXISTS idx_telemetry_queue_status ON telemetry_queue(status)`,
		`CREATE INDEX IF NOT EXISTS idx_telemetry_queue_type ON telemetry_queue(item_type)`,
		`CREATE TABLE IF NOT EXISTS server_config (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			config_json TEXT NOT NULL,
			applied_at TEXT,
			created_at TEXT NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS ssh_sessions (
			id TEXT PRIMARY KEY,
			session_id TEXT,
			user TEXT NOT NULL,
			source_ip TEXT,
			source_port INTEGER,
			action TEXT NOT NULL,
			auth_method TEXT,
			timestamp TEXT NOT NULL,
			queued INTEGER NOT NULL DEFAULT 0
		)`,
		`CREATE INDEX IF NOT EXISTS idx_ssh_sessions_queued ON ssh_sessions(queued)`,
		`CREATE TABLE IF NOT EXISTS collector_state (
			collector_name TEXT PRIMARY KEY,
			last_run_at TEXT,
			state_json TEXT
		)`,
		`CREATE TABLE IF NOT EXISTS trusted_signing_keys (
			key_id INTEGER PRIMARY KEY,
			user_id INTEGER NOT NULL,
			public_key BLOB NOT NULL,
			synced_at TEXT NOT NULL
		)`,
		`CREATE TABLE IF NOT EXISTS command_nonces (
			nonce TEXT PRIMARY KEY,
			executed_at TEXT NOT NULL
		)`,
	}

	for _, stmt := range statements {
		if _, err := conn.Exec(stmt); err != nil {
			preview := stmt
			if len(preview) > 60 {
				preview = preview[:60]
			}

			return fmt.Errorf("executing migration %q: %w", preview, err)
		}
	}

	slog.Info("database migrations complete")
	return nil
}