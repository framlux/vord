// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package db

import (
	"database/sql"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Store provides data access operations for all tables.
type Store struct {
	db           *sql.DB
	pendingCount atomic.Int64
	maxQueueSize int

	// countMu protects countLoaded and countInitErr so that the pending count
	// can be safely invalidated and reloaded (unlike sync.Once, which cannot be
	// reset without a data race).
	countMu      sync.Mutex
	countLoaded  bool
	countInitErr error
}

// defaultMaxQueueSize is used when no explicit max is provided.
const defaultMaxQueueSize = 10000

// NewStore creates a new Store backed by the given database connection.
// The maxQueueSize parameter controls the maximum number of pending telemetry
// items in the queue. When the queue is at capacity, the oldest pending items
// are evicted (FIFO) to make room for new telemetry. Pass 0 to use the default
// of 10000.
func NewStore(db *sql.DB, maxQueueSize ...int) *Store {
	size := defaultMaxQueueSize
	if len(maxQueueSize) > 0 && maxQueueSize[0] > 0 {
		size = maxQueueSize[0]
	}

	return &Store{db: db, maxQueueSize: size}
}

// loadPendingCount initializes the cached pending count from the database if not already loaded.
func (s *Store) loadPendingCount() (int64, error) {
	s.countMu.Lock()
	defer s.countMu.Unlock()

	if s.countLoaded == false {
		var count int64
		if err := s.db.QueryRow("SELECT COUNT(*) FROM telemetry_queue WHERE status = ?", int(QueueStatusPending)).Scan(&count); err != nil {
			s.countInitErr = fmt.Errorf("loading pending count: %w", err)

			return 0, s.countInitErr
		}
		s.pendingCount.Store(count)
		s.countLoaded = true
		s.countInitErr = nil
	}

	if s.countInitErr != nil {
		return 0, s.countInitErr
	}

	return s.pendingCount.Load(), nil
}

// --- agent_config ---

// GetConfig retrieves a config value by key.
func (s *Store) GetConfig(key string) (string, error) {
	var value string
	err := s.db.QueryRow("SELECT value FROM agent_config WHERE key = ?", key).Scan(&value)
	if err == sql.ErrNoRows {
		return "", nil
	}
	return value, err
}

// SetConfig upserts a config key-value pair.
func (s *Store) SetConfig(key, value string) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		`INSERT INTO agent_config (key, value, updated_at) VALUES (?, ?, ?)
		 ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at`,
		key, value, now,
	)
	return err
}

// --- telemetry_queue ---

// EnqueueTelemetry inserts a new telemetry item into the queue.
// If the queue has reached its configured maximum size, the oldest pending items
// are evicted (FIFO) to make room. This prevents unbounded queue growth during
// prolonged server outages while ensuring the newest telemetry is always retained.
func (s *Store) EnqueueTelemetry(id string, itemType TelemetryType, payload string) error {
	count, err := s.loadPendingCount()
	if err != nil {
		return fmt.Errorf("checking queue depth: %w", err)
	}

	// Evict the oldest pending items when the queue is at capacity so new
	// telemetry can always be enqueued. This keeps the most recent data and
	// discards stale entries that accumulated while the server was unreachable.
	if count >= int64(s.maxQueueSize) {
		evictCount := count - int64(s.maxQueueSize) + 1
		evicted, evictErr := s.evictOldestPending(evictCount)
		if evictErr != nil {
			return fmt.Errorf("evicting oldest telemetry: %w", evictErr)
		}
		s.pendingCount.Add(-evicted)
		slog.Warn("telemetry queue at capacity, evicted oldest items",
			"evicted", evicted,
			"max_queue_size", s.maxQueueSize,
		)
	}

	now := time.Now().UTC().Format(time.RFC3339)
	_, err = s.db.Exec(
		`INSERT INTO telemetry_queue (id, item_type, payload, status, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?)`,
		id, int(itemType), payload, int(QueueStatusPending), now, now,
	)
	if err != nil {
		if isSQLiteFull(err) {
			slog.Warn("SQLite database disk full, dropping telemetry item")

			return fmt.Errorf("database disk full: %w", err)
		}

		return err
	}

	s.pendingCount.Add(1)

	return nil
}

// evictOldestPending deletes the N oldest pending items from the telemetry queue.
// Returns the number of rows actually deleted.
func (s *Store) evictOldestPending(n int64) (int64, error) {
	result, err := s.db.Exec(
		`DELETE FROM telemetry_queue WHERE id IN (
			SELECT id FROM telemetry_queue
			WHERE status = ?
			ORDER BY created_at ASC
			LIMIT ?
		)`,
		int(QueueStatusPending), n,
	)
	if err != nil {
		return 0, err
	}

	return result.RowsAffected()
}

// isSQLiteFull checks if an error indicates the SQLite database is full.
func isSQLiteFull(err error) bool {
	if err == nil {
		return false
	}
	errStr := err.Error()

	return strings.Contains(errStr, "database or disk is full") ||
		strings.Contains(errStr, "SQLITE_FULL")
}

// DequeueTelemetry fetches up to limit pending items and marks them as processing.
func (s *Store) DequeueTelemetry(limit int) ([]TelemetryQueueItem, error) {
	return s.DequeueTelemetryByTypes(nil, limit)
}

// UpdateTelemetryStatus updates the status of a telemetry queue item.
func (s *Store) UpdateTelemetryStatus(id string, status QueueStatus) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		"UPDATE telemetry_queue SET status = ?, updated_at = ? WHERE id = ?",
		int(status), now, id,
	)
	return err
}

// PurgeTelemetry removes completed items older than the given duration.
func (s *Store) PurgeTelemetry(olderThan time.Duration) (int64, error) {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	result, err := s.db.Exec(
		"DELETE FROM telemetry_queue WHERE status = ? AND updated_at < ?",
		int(QueueStatusCompleted), cutoff,
	)
	if err != nil {
		return 0, err
	}
	return result.RowsAffected()
}

// --- server_config ---

// SaveServerConfig stores a new server configuration and prunes old entries.
func (s *Store) SaveServerConfig(configJSON string) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		"INSERT INTO server_config (config_json, created_at) VALUES (?, ?)",
		configJSON, now,
	)
	if err != nil {
		return err
	}
	// Keep only the 100 most recent configs to prevent unbounded growth.
	if _, err := s.db.Exec("DELETE FROM server_config WHERE id NOT IN (SELECT id FROM server_config ORDER BY id DESC LIMIT 100)"); err != nil {
		slog.Warn("failed to prune old server configs", "error", err)
	}
	return nil
}

// GetLatestServerConfig returns the most recent server config.
func (s *Store) GetLatestServerConfig() (*ServerConfig, error) {
	var cfg ServerConfig
	err := s.db.QueryRow(
		"SELECT id, config_json, applied_at, created_at FROM server_config ORDER BY id DESC LIMIT 1",
	).Scan(&cfg.ID, &cfg.ConfigJSON, &cfg.AppliedAt, &cfg.CreatedAt)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &cfg, nil
}

// MarkServerConfigApplied sets the applied_at timestamp on a server config row.
func (s *Store) MarkServerConfigApplied(id int64) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec("UPDATE server_config SET applied_at = ? WHERE id = ?", now, id)
	return err
}

// --- ssh_sessions ---

// InsertSSHSession stores a parsed SSH session event.
func (s *Store) InsertSSHSession(session *SSHSession) error {
	_, err := s.db.Exec(
		`INSERT INTO ssh_sessions (id, session_id, user, source_ip, source_port, action, auth_method, timestamp, queued)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		session.ID, session.SessionID, session.User, session.SourceIP,
		session.SourcePort, session.Action, session.AuthMethod, session.Timestamp, session.Queued,
	)
	return err
}

// GetUnqueuedSSHSessions returns sessions not yet copied to telemetry_queue.
func (s *Store) GetUnqueuedSSHSessions() ([]SSHSession, error) {
	rows, err := s.db.Query(
		"SELECT id, session_id, user, source_ip, source_port, action, auth_method, timestamp, queued FROM ssh_sessions WHERE queued = 0",
	)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var sessions []SSHSession
	for rows.Next() {
		var sess SSHSession
		if err := rows.Scan(&sess.ID, &sess.SessionID, &sess.User, &sess.SourceIP,
			&sess.SourcePort, &sess.Action, &sess.AuthMethod, &sess.Timestamp, &sess.Queued); err != nil {
			return nil, err
		}
		sessions = append(sessions, sess)
	}
	return sessions, rows.Err()
}

// MarkSSHSessionQueued marks a session as queued to telemetry_queue.
func (s *Store) MarkSSHSessionQueued(id string) error {
	_, err := s.db.Exec("UPDATE ssh_sessions SET queued = 1 WHERE id = ?", id)

	return err
}

// InsertSSHSessionAndEnqueue atomically inserts an SSH session, enqueues the
// telemetry item, and marks the session as queued within a single transaction.
// This prevents inconsistent state if the agent crashes between any of these steps.
func (s *Store) InsertSSHSessionAndEnqueue(session *SSHSession, telemetryID string, telemetryPayload string) error {
	tx, err := s.db.Begin()
	if err != nil {
		return fmt.Errorf("begin ssh session transaction: %w", err)
	}
	defer tx.Rollback()

	// Insert the SSH session record.
	if _, err := tx.Exec(
		`INSERT INTO ssh_sessions (id, session_id, user, source_ip, source_port, action, auth_method, timestamp, queued)
		 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		session.ID, session.SessionID, session.User, session.SourceIP,
		session.SourcePort, session.Action, session.AuthMethod, session.Timestamp, 0,
	); err != nil {
		return fmt.Errorf("inserting ssh session: %w", err)
	}

	// Enqueue the telemetry item.
	now := time.Now().UTC().Format(time.RFC3339)
	if _, err := tx.Exec(
		`INSERT INTO telemetry_queue (id, item_type, payload, status, created_at, updated_at)
		 VALUES (?, ?, ?, ?, ?, ?)`,
		telemetryID, int(TelemetrySSHSession), telemetryPayload, int(QueueStatusPending), now, now,
	); err != nil {
		return fmt.Errorf("enqueuing ssh telemetry: %w", err)
	}

	// Mark the session as queued.
	if _, err := tx.Exec("UPDATE ssh_sessions SET queued = 1 WHERE id = ?", session.ID); err != nil {
		return fmt.Errorf("marking ssh session queued: %w", err)
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("committing ssh session transaction: %w", err)
	}

	// Update the cached pending count.
	s.pendingCount.Add(1)

	return nil
}

// PurgeSSHSessions removes SSH sessions older than the given duration.
func (s *Store) PurgeSSHSessions(olderThan time.Duration) (int64, error) {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	result, err := s.db.Exec(
		"DELETE FROM ssh_sessions WHERE timestamp < ?",
		cutoff,
	)
	if err != nil {
		return 0, err
	}

	return result.RowsAffected()
}

// --- collector_state ---

// GetCollectorState retrieves the state for a named collector.
func (s *Store) GetCollectorState(name string) (*CollectorState, error) {
	var cs CollectorState
	err := s.db.QueryRow(
		"SELECT collector_name, last_run_at, state_json FROM collector_state WHERE collector_name = ?", name,
	).Scan(&cs.CollectorName, &cs.LastRunAt, &cs.StateJSON)
	if err == sql.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &cs, nil
}

// SaveCollectorState upserts the state for a named collector.
func (s *Store) SaveCollectorState(name string, stateJSON *string) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		`INSERT INTO collector_state (collector_name, last_run_at, state_json) VALUES (?, ?, ?)
		 ON CONFLICT(collector_name) DO UPDATE SET last_run_at = excluded.last_run_at, state_json = excluded.state_json`,
		name, now, stateJSON,
	)
	return err
}

// DB returns the underlying database connection.
func (s *Store) DB() *sql.DB {
	return s.db
}

// Close closes the underlying database connection.
func (s *Store) Close() error {
	return s.db.Close()
}

// DequeueTelemetryByTypes fetches up to limit pending items and marks them as processing.
// If types is nil, all pending items are dequeued regardless of type.
// If types is non-nil but empty, no items are returned.
func (s *Store) DequeueTelemetryByTypes(types []TelemetryType, limit int) ([]TelemetryQueueItem, error) {
	if types != nil && len(types) == 0 {
		return nil, nil
	}

	// Ensure pending count is initialized before we decrement it.
	if _, err := s.loadPendingCount(); err != nil {
		return nil, fmt.Errorf("initializing pending count: %w", err)
	}

	tx, err := s.db.Begin()
	if err != nil {
		return nil, err
	}
	defer tx.Rollback()

	// Build the SELECT query with optional type filter.
	var query string
	var args []any
	if types == nil {
		query = `SELECT id, item_type, payload, status, created_at, updated_at
			 FROM telemetry_queue WHERE status = ? ORDER BY created_at ASC LIMIT ?`
		args = []any{int(QueueStatusPending), limit}
	} else {
		placeholders := make([]string, len(types))
		args = make([]any, 0, len(types)+2)
		args = append(args, int(QueueStatusPending))
		for i, t := range types {
			placeholders[i] = "?"
			args = append(args, int(t))
		}
		args = append(args, limit)
		query = fmt.Sprintf(
			`SELECT id, item_type, payload, status, created_at, updated_at
			 FROM telemetry_queue WHERE status = ? AND item_type IN (%s) ORDER BY created_at ASC LIMIT ?`,
			strings.Join(placeholders, ","),
		)
	}

	rows, err := tx.Query(query, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var items []TelemetryQueueItem
	var ids []any
	for rows.Next() {
		var item TelemetryQueueItem
		var itemType, status int
		if err := rows.Scan(&item.ID, &itemType, &item.Payload, &status, &item.CreatedAt, &item.UpdatedAt); err != nil {
			return nil, err
		}
		item.ItemType = TelemetryType(itemType)
		item.Status = QueueStatus(status)
		items = append(items, item)
		ids = append(ids, item.ID)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	if len(ids) > 0 {
		now := time.Now().UTC().Format(time.RFC3339)
		idPlaceholders := make([]string, len(ids))
		updateArgs := make([]any, 0, len(ids)+2)
		updateArgs = append(updateArgs, int(QueueStatusProcessing), now)
		for i, id := range ids {
			idPlaceholders[i] = "?"
			updateArgs = append(updateArgs, id)
		}
		updateQuery := fmt.Sprintf(
			"UPDATE telemetry_queue SET status = ?, updated_at = ? WHERE id IN (%s)",
			strings.Join(idPlaceholders, ","),
		)
		if _, err := tx.Exec(updateQuery, updateArgs...); err != nil {
			return nil, err
		}
	}

	if err := tx.Commit(); err != nil {
		return nil, err
	}

	s.pendingCount.Add(-int64(len(ids)))

	return items, nil
}

// ResetProcessingTelemetry marks all processing items back to pending (used on startup for crash recovery).
func (s *Store) ResetProcessingTelemetry() error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		"UPDATE telemetry_queue SET status = ?, updated_at = ? WHERE status = ?",
		int(QueueStatusPending), now, int(QueueStatusProcessing),
	)
	if err != nil {
		return err
	}

	// Invalidate the cached count so the next access reloads from the database.
	s.countMu.Lock()
	s.countLoaded = false
	s.countInitErr = nil
	s.countMu.Unlock()

	return nil
}

// MarkTelemetryCompleted marks multiple items as completed.
func (s *Store) MarkTelemetryCompleted(ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	now := time.Now().UTC().Format(time.RFC3339)
	placeholders := make([]string, len(ids))
	args := make([]any, 0, len(ids)+2)
	args = append(args, int(QueueStatusCompleted), now)
	for i, id := range ids {
		placeholders[i] = "?"
		args = append(args, id)
	}
	query := fmt.Sprintf(
		"UPDATE telemetry_queue SET status = ?, updated_at = ? WHERE id IN (%s)",
		strings.Join(placeholders, ","),
	)
	_, err := s.db.Exec(query, args...)

	return err
}

// MarkTelemetryPending marks multiple items back to pending (for retry).
func (s *Store) MarkTelemetryPending(ids []string) error {
	if len(ids) == 0 {
		return nil
	}
	now := time.Now().UTC().Format(time.RFC3339)
	placeholders := make([]string, len(ids))
	args := make([]any, 0, len(ids)+2)
	args = append(args, int(QueueStatusPending), now)
	for i, id := range ids {
		placeholders[i] = "?"
		args = append(args, id)
	}
	query := fmt.Sprintf(
		"UPDATE telemetry_queue SET status = ?, updated_at = ? WHERE id IN (%s)",
		strings.Join(placeholders, ","),
	)
	_, err := s.db.Exec(query, args...)
	if err != nil {
		return err
	}

	s.pendingCount.Add(int64(len(ids)))

	return err
}

// CountPendingTelemetry returns the number of pending telemetry items.
func (s *Store) CountPendingTelemetry() (int64, error) {
	return s.loadPendingCount()
}

// --- Trusted Signing Keys ---

// TrustedKey represents a cached signing key for command signature verification.
type TrustedKey struct {
	KeyID     int32
	UserID    int32
	PublicKey []byte
}

// UpsertSigningKeys replaces all trusted signing keys with the provided set.
func (s *Store) UpsertSigningKeys(keys []TrustedKey) error {
	tx, err := s.db.Begin()
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback()

	if _, err := tx.Exec("DELETE FROM trusted_signing_keys"); err != nil {
		return fmt.Errorf("clearing signing keys: %w", err)
	}

	now := time.Now().UTC().Format(time.RFC3339)
	for _, k := range keys {
		if _, err := tx.Exec(
			"INSERT INTO trusted_signing_keys (key_id, user_id, public_key, synced_at) VALUES (?, ?, ?, ?)",
			k.KeyID, k.UserID, k.PublicKey, now,
		); err != nil {
			return fmt.Errorf("inserting signing key %d: %w", k.KeyID, err)
		}
	}

	return tx.Commit()
}

// GetSigningKey retrieves a trusted signing key by its ID.
func (s *Store) GetSigningKey(keyID int32) (*TrustedKey, error) {
	var k TrustedKey
	err := s.db.QueryRow(
		"SELECT key_id, user_id, public_key FROM trusted_signing_keys WHERE key_id = ?",
		keyID,
	).Scan(&k.KeyID, &k.UserID, &k.PublicKey)
	if err != nil {
		return nil, err
	}

	return &k, nil
}

// --- Command Nonces ---

// IsNonceUsed checks whether a nonce has already been executed.
func (s *Store) IsNonceUsed(nonce string) (bool, error) {
	var count int
	err := s.db.QueryRow("SELECT COUNT(*) FROM command_nonces WHERE nonce = ?", nonce).Scan(&count)
	if err != nil {
		return false, err
	}

	return count > 0, nil
}

// RecordNonce stores a nonce as executed.
func (s *Store) RecordNonce(nonce string) error {
	now := time.Now().UTC().Format(time.RFC3339)
	_, err := s.db.Exec(
		"INSERT OR IGNORE INTO command_nonces (nonce, executed_at) VALUES (?, ?)",
		nonce, now,
	)

	return err
}

// PurgeNonces removes nonces older than the given duration.
func (s *Store) PurgeNonces(olderThan time.Duration) (int64, error) {
	cutoff := time.Now().UTC().Add(-olderThan).Format(time.RFC3339)
	result, err := s.db.Exec("DELETE FROM command_nonces WHERE executed_at < ?", cutoff)
	if err != nil {
		return 0, err
	}

	return result.RowsAffected()
}