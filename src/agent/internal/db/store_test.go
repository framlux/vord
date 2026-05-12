// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package db

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

func newTestStore(t *testing.T) *Store {
	t.Helper()
	db, err := Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { db.Close() })
	store := NewStore(db, 0)

	return store
}

// --- agent_config tests ---

func TestGetConfig_MissingKey_ReturnsEmptyString(t *testing.T) {
	store := newTestStore(t)

	val, err := store.GetConfig("nonexistent")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if val != "" {
		t.Errorf("expected empty string, got %q", val)
	}
}

func TestSetConfig_AndGetConfig_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	err := store.SetConfig("api_key", "secret-123")
	if err != nil {
		t.Fatalf("SetConfig: %v", err)
	}

	val, err := store.GetConfig("api_key")
	if err != nil {
		t.Fatalf("GetConfig: %v", err)
	}
	if val != "secret-123" {
		t.Errorf("expected %q, got %q", "secret-123", val)
	}
}

func TestSetConfig_UpdatesExistingValue(t *testing.T) {
	store := newTestStore(t)

	err := store.SetConfig("key1", "original")
	if err != nil {
		t.Fatalf("SetConfig first call: %v", err)
	}

	err = store.SetConfig("key1", "updated")
	if err != nil {
		t.Fatalf("SetConfig second call: %v", err)
	}

	val, err := store.GetConfig("key1")
	if err != nil {
		t.Fatalf("GetConfig: %v", err)
	}
	if val != "updated" {
		t.Errorf("expected %q, got %q", "updated", val)
	}
}

// --- telemetry_queue tests ---

func TestEnqueueTelemetry_AndDequeueTelemetry_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("telem-1", TelemetryCpuUsage, `{"cpu":42}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 item, got %d", len(items))
	}

	item := items[0]
	if item.ID != "telem-1" {
		t.Errorf("expected ID %q, got %q", "telem-1", item.ID)
	}
	if item.ItemType != TelemetryCpuUsage {
		t.Errorf("expected ItemType %d, got %d", TelemetryCpuUsage, item.ItemType)
	}
	if item.Payload != `{"cpu":42}` {
		t.Errorf("expected payload %q, got %q", `{"cpu":42}`, item.Payload)
	}
	// The returned items should still show Pending status (they were scanned before the update).
	if item.Status != QueueStatusPending {
		t.Errorf("expected status %d (Pending), got %d", QueueStatusPending, item.Status)
	}
}

func TestDequeueTelemetry_MarksItemsAsProcessing(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("telem-proc-1", TelemetryMemoryInfo, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}

	// Dequeue again should return nothing because items are now Processing.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry second call: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items on second dequeue, got %d", len(items))
	}
}

func TestDequeueTelemetryByTypes_FiltersCorrectly(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("t-cpu", TelemetryCpuUsage, `{"cpu":1}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry cpu: %v", err)
	}
	err = store.EnqueueTelemetry("t-mem", TelemetryMemoryUsage, `{"mem":2}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry mem: %v", err)
	}
	err = store.EnqueueTelemetry("t-disk", TelemetryDiskUsage, `{"disk":3}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry disk: %v", err)
	}

	// Only request CPU and Disk types.
	items, err := store.DequeueTelemetryByTypes([]TelemetryType{TelemetryCpuUsage, TelemetryDiskUsage}, 10)
	if err != nil {
		t.Fatalf("DequeueTelemetryByTypes: %v", err)
	}
	if len(items) != 2 {
		t.Fatalf("expected 2 items, got %d", len(items))
	}

	ids := map[string]bool{}
	for _, item := range items {
		ids[item.ID] = true
	}
	if ids["t-cpu"] == false {
		t.Error("expected t-cpu in results")
	}
	if ids["t-disk"] == false {
		t.Error("expected t-disk in results")
	}
	if ids["t-mem"] == true {
		t.Error("t-mem should not be in results")
	}
}

func TestDequeueTelemetryByTypes_RespectsLimit(t *testing.T) {
	store := newTestStore(t)

	for i := 0; i < 5; i++ {
		err := store.EnqueueTelemetry("lim-"+string(rune('a'+i)), TelemetryCpuUsage, `{}`)
		if err != nil {
			t.Fatalf("EnqueueTelemetry: %v", err)
		}
	}

	items, err := store.DequeueTelemetryByTypes([]TelemetryType{TelemetryCpuUsage}, 2)
	if err != nil {
		t.Fatalf("DequeueTelemetryByTypes: %v", err)
	}
	if len(items) != 2 {
		t.Errorf("expected 2 items, got %d", len(items))
	}
}

func TestMarkTelemetryCompleted_UpdatesStatus(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("comp-1", TelemetryCpuInfo, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}

	err = store.MarkTelemetryCompleted([]string{"comp-1"})
	if err != nil {
		t.Fatalf("MarkTelemetryCompleted: %v", err)
	}

	// Nothing should be pending or processing now.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry after complete: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items after marking completed, got %d", len(items))
	}

	count, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 0 {
		t.Errorf("expected 0 pending, got %d", count)
	}
}

func TestMarkTelemetryCompleted_EmptySlice(t *testing.T) {
	store := newTestStore(t)

	err := store.MarkTelemetryCompleted([]string{})
	if err != nil {
		t.Fatalf("MarkTelemetryCompleted with empty slice: %v", err)
	}
}

func TestMarkTelemetryPending_RevertsStatus(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("pend-1", TelemetryDiskInfo, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Dequeue moves to Processing.
	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}

	// Mark back to Pending.
	err = store.MarkTelemetryPending([]string{"pend-1"})
	if err != nil {
		t.Fatalf("MarkTelemetryPending: %v", err)
	}

	// Now dequeue should return it again.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry after pending: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 item after marking pending, got %d", len(items))
	}
}

func TestMarkTelemetryPending_EmptySlice(t *testing.T) {
	store := newTestStore(t)

	err := store.MarkTelemetryPending([]string{})
	if err != nil {
		t.Fatalf("MarkTelemetryPending with empty slice: %v", err)
	}
}

func TestResetProcessingTelemetry_RevertsProcessingToPending(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("reset-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}
	err = store.EnqueueTelemetry("reset-2", TelemetryMemoryUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Dequeue moves both to Processing.
	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}

	// Reset all processing back to pending.
	err = store.ResetProcessingTelemetry()
	if err != nil {
		t.Fatalf("ResetProcessingTelemetry: %v", err)
	}

	// Now they should be dequeueable again.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry after reset: %v", err)
	}
	if len(items) != 2 {
		t.Errorf("expected 2 items after reset, got %d", len(items))
	}
}

func TestCountPendingTelemetry_ReturnsCorrectCount(t *testing.T) {
	store := newTestStore(t)

	count, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 0 {
		t.Errorf("expected 0 pending on empty db, got %d", count)
	}

	err = store.EnqueueTelemetry("cnt-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}
	err = store.EnqueueTelemetry("cnt-2", TelemetryMemoryUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}
	err = store.EnqueueTelemetry("cnt-3", TelemetryDiskUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	count, err = store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 3 {
		t.Errorf("expected 3 pending, got %d", count)
	}

	// Dequeue one item (moves to Processing).
	_, err = store.DequeueTelemetry(1)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}

	count, err = store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry after dequeue: %v", err)
	}
	if count != 2 {
		t.Errorf("expected 2 pending after dequeue, got %d", count)
	}
}

func TestPurgeTelemetry_RemovesOldCompletedItems(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("purge-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}
	err = store.EnqueueTelemetry("purge-2", TelemetryMemoryUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Dequeue and mark completed.
	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	err = store.MarkTelemetryCompleted([]string{"purge-1", "purge-2"})
	if err != nil {
		t.Fatalf("MarkTelemetryCompleted: %v", err)
	}

	// Backdate the updated_at to make items look old.
	oldTime := time.Now().UTC().Add(-48 * time.Hour).Format(time.RFC3339)
	_, err = store.DB().Exec("UPDATE telemetry_queue SET updated_at = ?", oldTime)
	if err != nil {
		t.Fatalf("backdating items: %v", err)
	}

	// Purge items older than 24 hours.
	deleted, err := store.PurgeTelemetry(24 * time.Hour)
	if err != nil {
		t.Fatalf("PurgeTelemetry: %v", err)
	}
	if deleted != 2 {
		t.Errorf("expected 2 deleted, got %d", deleted)
	}
}

func TestPurgeTelemetry_DoesNotRemoveRecentItems(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("recent-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	_, err = store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	err = store.MarkTelemetryCompleted([]string{"recent-1"})
	if err != nil {
		t.Fatalf("MarkTelemetryCompleted: %v", err)
	}

	// Purge items older than 24 hours -- recent item should survive.
	deleted, err := store.PurgeTelemetry(24 * time.Hour)
	if err != nil {
		t.Fatalf("PurgeTelemetry: %v", err)
	}
	if deleted != 0 {
		t.Errorf("expected 0 deleted for recent items, got %d", deleted)
	}
}

func TestPurgeTelemetry_DoesNotRemovePendingItems(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("pending-purge", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Backdate the item but leave it Pending.
	oldTime := time.Now().UTC().Add(-48 * time.Hour).Format(time.RFC3339)
	_, err = store.DB().Exec("UPDATE telemetry_queue SET updated_at = ?", oldTime)
	if err != nil {
		t.Fatalf("backdating items: %v", err)
	}

	deleted, err := store.PurgeTelemetry(24 * time.Hour)
	if err != nil {
		t.Fatalf("PurgeTelemetry: %v", err)
	}
	if deleted != 0 {
		t.Errorf("expected 0 deleted for pending items, got %d", deleted)
	}
}

func TestUpdateTelemetryStatus(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("status-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	err = store.UpdateTelemetryStatus("status-1", QueueStatusFailed)
	if err != nil {
		t.Fatalf("UpdateTelemetryStatus: %v", err)
	}

	// Item is now Failed, not Pending, so dequeue should return nothing.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items after marking failed, got %d", len(items))
	}
}

// --- server_config tests ---

func TestSaveServerConfig_AndGetLatestServerConfig_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	// No config initially.
	cfg, err := store.GetLatestServerConfig()
	if err != nil {
		t.Fatalf("GetLatestServerConfig: %v", err)
	}
	if cfg != nil {
		t.Errorf("expected nil for empty table, got %+v", cfg)
	}

	err = store.SaveServerConfig(`{"interval":30}`)
	if err != nil {
		t.Fatalf("SaveServerConfig: %v", err)
	}

	cfg, err = store.GetLatestServerConfig()
	if err != nil {
		t.Fatalf("GetLatestServerConfig: %v", err)
	}
	if cfg == nil {
		t.Fatal("expected non-nil config")
	}
	if cfg.ConfigJSON != `{"interval":30}` {
		t.Errorf("expected config JSON %q, got %q", `{"interval":30}`, cfg.ConfigJSON)
	}
	if cfg.AppliedAt != nil {
		t.Errorf("expected nil applied_at, got %v", cfg.AppliedAt)
	}
}

func TestGetLatestServerConfig_ReturnsNewest(t *testing.T) {
	store := newTestStore(t)

	err := store.SaveServerConfig(`{"version":1}`)
	if err != nil {
		t.Fatalf("SaveServerConfig v1: %v", err)
	}
	err = store.SaveServerConfig(`{"version":2}`)
	if err != nil {
		t.Fatalf("SaveServerConfig v2: %v", err)
	}

	cfg, err := store.GetLatestServerConfig()
	if err != nil {
		t.Fatalf("GetLatestServerConfig: %v", err)
	}
	if cfg == nil {
		t.Fatal("expected non-nil config")
	}
	if cfg.ConfigJSON != `{"version":2}` {
		t.Errorf("expected latest config, got %q", cfg.ConfigJSON)
	}
}

func TestMarkServerConfigApplied_UpdatesAppliedAt(t *testing.T) {
	store := newTestStore(t)

	err := store.SaveServerConfig(`{"data":"test"}`)
	if err != nil {
		t.Fatalf("SaveServerConfig: %v", err)
	}

	cfg, err := store.GetLatestServerConfig()
	if err != nil {
		t.Fatalf("GetLatestServerConfig: %v", err)
	}

	err = store.MarkServerConfigApplied(cfg.ID)
	if err != nil {
		t.Fatalf("MarkServerConfigApplied: %v", err)
	}

	cfg, err = store.GetLatestServerConfig()
	if err != nil {
		t.Fatalf("GetLatestServerConfig after apply: %v", err)
	}
	if cfg.AppliedAt == nil {
		t.Error("expected applied_at to be set")
	}
}

// --- ssh_sessions tests ---

func TestInsertSSHSession_AndGetUnqueuedSSHSessions_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	sessionID := "sess-abc"
	sourceIP := "192.168.1.10"
	sourcePort := 22
	authMethod := "publickey"

	session := &SSHSession{
		ID:         "ssh-1",
		SessionID:  &sessionID,
		User:       "root",
		SourceIP:   &sourceIP,
		SourcePort: &sourcePort,
		Action:     "login",
		AuthMethod: &authMethod,
		Timestamp:  time.Now().UTC().Format(time.RFC3339),
		Queued:     0,
	}

	err := store.InsertSSHSession(session)
	if err != nil {
		t.Fatalf("InsertSSHSession: %v", err)
	}

	sessions, err := store.GetUnqueuedSSHSessions()
	if err != nil {
		t.Fatalf("GetUnqueuedSSHSessions: %v", err)
	}
	if len(sessions) != 1 {
		t.Fatalf("expected 1 session, got %d", len(sessions))
	}

	got := sessions[0]
	if got.ID != "ssh-1" {
		t.Errorf("expected ID %q, got %q", "ssh-1", got.ID)
	}
	if got.User != "root" {
		t.Errorf("expected user %q, got %q", "root", got.User)
	}
	if got.Action != "login" {
		t.Errorf("expected action %q, got %q", "login", got.Action)
	}
	if got.SessionID == nil || *got.SessionID != "sess-abc" {
		t.Errorf("expected session_id %q, got %v", "sess-abc", got.SessionID)
	}
	if got.SourceIP == nil || *got.SourceIP != "192.168.1.10" {
		t.Errorf("expected source_ip %q, got %v", "192.168.1.10", got.SourceIP)
	}
	if got.SourcePort == nil || *got.SourcePort != 22 {
		t.Errorf("expected source_port %d, got %v", 22, got.SourcePort)
	}
	if got.AuthMethod == nil || *got.AuthMethod != "publickey" {
		t.Errorf("expected auth_method %q, got %v", "publickey", got.AuthMethod)
	}
}

func TestInsertSSHSession_WithNilOptionalFields(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:         "ssh-nil",
		SessionID:  nil,
		User:       "admin",
		SourceIP:   nil,
		SourcePort: nil,
		Action:     "logout",
		AuthMethod: nil,
		Timestamp:  time.Now().UTC().Format(time.RFC3339),
		Queued:     0,
	}

	err := store.InsertSSHSession(session)
	if err != nil {
		t.Fatalf("InsertSSHSession with nils: %v", err)
	}

	sessions, err := store.GetUnqueuedSSHSessions()
	if err != nil {
		t.Fatalf("GetUnqueuedSSHSessions: %v", err)
	}
	if len(sessions) != 1 {
		t.Fatalf("expected 1 session, got %d", len(sessions))
	}

	got := sessions[0]
	if got.SessionID != nil {
		t.Errorf("expected nil session_id, got %v", got.SessionID)
	}
	if got.SourceIP != nil {
		t.Errorf("expected nil source_ip, got %v", got.SourceIP)
	}
	if got.SourcePort != nil {
		t.Errorf("expected nil source_port, got %v", got.SourcePort)
	}
	if got.AuthMethod != nil {
		t.Errorf("expected nil auth_method, got %v", got.AuthMethod)
	}
}

func TestMarkSSHSessionQueued_UpdatesQueuedFlag(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:        "ssh-q-1",
		User:      "root",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
		Queued:    0,
	}

	err := store.InsertSSHSession(session)
	if err != nil {
		t.Fatalf("InsertSSHSession: %v", err)
	}

	err = store.MarkSSHSessionQueued("ssh-q-1")
	if err != nil {
		t.Fatalf("MarkSSHSessionQueued: %v", err)
	}

	// Should no longer appear in unqueued list.
	sessions, err := store.GetUnqueuedSSHSessions()
	if err != nil {
		t.Fatalf("GetUnqueuedSSHSessions: %v", err)
	}
	if len(sessions) != 0 {
		t.Errorf("expected 0 unqueued sessions, got %d", len(sessions))
	}
}

func TestGetUnqueuedSSHSessions_ExcludesQueued(t *testing.T) {
	store := newTestStore(t)

	unqueued := &SSHSession{
		ID:        "ssh-unq",
		User:      "user1",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
		Queued:    0,
	}
	queued := &SSHSession{
		ID:        "ssh-queued",
		User:      "user2",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
		Queued:    1,
	}

	err := store.InsertSSHSession(unqueued)
	if err != nil {
		t.Fatalf("InsertSSHSession unqueued: %v", err)
	}
	err = store.InsertSSHSession(queued)
	if err != nil {
		t.Fatalf("InsertSSHSession queued: %v", err)
	}

	sessions, err := store.GetUnqueuedSSHSessions()
	if err != nil {
		t.Fatalf("GetUnqueuedSSHSessions: %v", err)
	}
	if len(sessions) != 1 {
		t.Fatalf("expected 1 unqueued session, got %d", len(sessions))
	}
	if sessions[0].ID != "ssh-unq" {
		t.Errorf("expected %q, got %q", "ssh-unq", sessions[0].ID)
	}
}

// --- collector_state tests ---

func TestGetCollectorState_MissingCollector_ReturnsNil(t *testing.T) {
	store := newTestStore(t)

	cs, err := store.GetCollectorState("nonexistent")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs != nil {
		t.Errorf("expected nil for missing collector, got %+v", cs)
	}
}

func TestSaveCollectorState_AndGetCollectorState_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	stateJSON := `{"last_offset":42}`
	err := store.SaveCollectorState("cpu_collector", &stateJSON)
	if err != nil {
		t.Fatalf("SaveCollectorState: %v", err)
	}

	cs, err := store.GetCollectorState("cpu_collector")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs == nil {
		t.Fatal("expected non-nil collector state")
	}
	if cs.CollectorName != "cpu_collector" {
		t.Errorf("expected collector_name %q, got %q", "cpu_collector", cs.CollectorName)
	}
	if cs.StateJSON == nil || *cs.StateJSON != `{"last_offset":42}` {
		t.Errorf("expected state_json %q, got %v", `{"last_offset":42}`, cs.StateJSON)
	}
	if cs.LastRunAt == nil {
		t.Error("expected last_run_at to be set")
	}
}

func TestSaveCollectorState_Upsert(t *testing.T) {
	store := newTestStore(t)

	state1 := `{"offset":1}`
	err := store.SaveCollectorState("disk_collector", &state1)
	if err != nil {
		t.Fatalf("SaveCollectorState first: %v", err)
	}

	state2 := `{"offset":99}`
	err = store.SaveCollectorState("disk_collector", &state2)
	if err != nil {
		t.Fatalf("SaveCollectorState upsert: %v", err)
	}

	cs, err := store.GetCollectorState("disk_collector")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs == nil {
		t.Fatal("expected non-nil collector state after upsert")
	}
	if cs.StateJSON == nil || *cs.StateJSON != `{"offset":99}` {
		t.Errorf("expected updated state_json %q, got %v", `{"offset":99}`, cs.StateJSON)
	}
}

func TestSaveCollectorState_NilStateJSON(t *testing.T) {
	store := newTestStore(t)

	err := store.SaveCollectorState("null_state_collector", nil)
	if err != nil {
		t.Fatalf("SaveCollectorState with nil: %v", err)
	}

	cs, err := store.GetCollectorState("null_state_collector")
	if err != nil {
		t.Fatalf("GetCollectorState: %v", err)
	}
	if cs == nil {
		t.Fatal("expected non-nil collector state")
	}
	if cs.StateJSON != nil {
		t.Errorf("expected nil state_json, got %v", cs.StateJSON)
	}
}

// --- DequeueTelemetry edge cases ---

func TestDequeueTelemetry_EmptyQueue(t *testing.T) {
	store := newTestStore(t)

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry on empty queue: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items from empty queue, got %d", len(items))
	}
}

func TestDequeueTelemetry_RespectsLimit(t *testing.T) {
	store := newTestStore(t)

	for i := 0; i < 5; i++ {
		id := "limit-" + string(rune('a'+i))
		err := store.EnqueueTelemetry(id, TelemetryCpuUsage, `{}`)
		if err != nil {
			t.Fatalf("EnqueueTelemetry: %v", err)
		}
	}

	items, err := store.DequeueTelemetry(3)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 3 {
		t.Errorf("expected 3 items, got %d", len(items))
	}
}

func TestDequeueTelemetryByTypes_EmptyTypes(t *testing.T) {
	store := newTestStore(t)

	err := store.EnqueueTelemetry("empty-types-1", TelemetryCpuUsage, `{}`)
	if err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Empty types slice generates an invalid SQL IN() clause, but the store
	// should either return an error or empty results. We verify it does not
	// panic.
	items, err := store.DequeueTelemetryByTypes([]TelemetryType{}, 10)
	if err != nil {
		// Acceptable -- empty IN() is invalid SQL in some drivers.
		return
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items with empty types filter, got %d", len(items))
	}
}

// --- Extended store tests (Priority 4) ---

// Intent: Enqueueing many items tracks the pending count correctly.
func TestEnqueueTelemetry_QueueOverflow(t *testing.T) {
	store := newTestStore(t)

	for i := 0; i < 100; i++ {
		id := "overflow-" + string(rune('a'+i%26)) + string(rune('0'+i/26))
		err := store.EnqueueTelemetry(id, TelemetryCpuUsage, `{}`)
		if err != nil {
			t.Fatalf("EnqueueTelemetry %d: %v", i, err)
		}
	}

	// Verify count is tracked correctly.
	count, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 100 {
		t.Errorf("expected 100 pending, got %d", count)
	}
}

// Intent: When the queue reaches its maximum size, the oldest pending items are
// evicted (FIFO) to make room for new telemetry, preventing unbounded growth.
func TestEnqueueTelemetry_FIFOEviction(t *testing.T) {
	database, err := Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	// Create a store with a small max queue size for testing eviction.
	store := NewStore(database, 5)

	// Fill the queue to capacity.
	for i := 0; i < 5; i++ {
		id := fmt.Sprintf("item-%d", i)
		if err := store.EnqueueTelemetry(id, TelemetryCpuUsage, fmt.Sprintf(`{"seq":%d}`, i)); err != nil {
			t.Fatalf("EnqueueTelemetry %d: %v", i, err)
		}
	}

	count, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 5 {
		t.Fatalf("expected 5 pending before eviction, got %d", count)
	}

	// Enqueue one more item; this should evict the oldest (item-0).
	if err := store.EnqueueTelemetry("item-5", TelemetryCpuUsage, `{"seq":5}`); err != nil {
		t.Fatalf("EnqueueTelemetry after capacity: %v", err)
	}

	count, err = store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry after eviction: %v", err)
	}
	if count != 5 {
		t.Errorf("expected 5 pending after eviction, got %d", count)
	}

	// Dequeue all items and verify the oldest was evicted.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 5 {
		t.Fatalf("expected 5 items, got %d", len(items))
	}

	// The first item should be item-1 (item-0 was evicted).
	if items[0].ID != "item-1" {
		t.Errorf("expected oldest remaining item to be %q, got %q", "item-1", items[0].ID)
	}
	// The last item should be the newly inserted item-5.
	if items[4].ID != "item-5" {
		t.Errorf("expected newest item to be %q, got %q", "item-5", items[4].ID)
	}
}

// Intent: Multiple evictions when queue is well over capacity still work correctly.
func TestEnqueueTelemetry_FIFOEviction_MultipleBurst(t *testing.T) {
	database, err := Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	store := NewStore(database, 3)

	// Fill queue to capacity.
	for i := 0; i < 3; i++ {
		id := fmt.Sprintf("burst-%d", i)
		if err := store.EnqueueTelemetry(id, TelemetryMemoryUsage, `{}`); err != nil {
			t.Fatalf("EnqueueTelemetry %d: %v", i, err)
		}
	}

	// Enqueue 3 more items, causing 3 evictions total.
	for i := 3; i < 6; i++ {
		id := fmt.Sprintf("burst-%d", i)
		if err := store.EnqueueTelemetry(id, TelemetryMemoryUsage, `{}`); err != nil {
			t.Fatalf("EnqueueTelemetry %d: %v", i, err)
		}
	}

	count, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry: %v", err)
	}
	if count != 3 {
		t.Errorf("expected 3 pending after burst evictions, got %d", count)
	}

	// Only the last 3 items should remain.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 3 {
		t.Fatalf("expected 3 items, got %d", len(items))
	}
	if items[0].ID != "burst-3" {
		t.Errorf("expected first remaining item %q, got %q", "burst-3", items[0].ID)
	}
	if items[2].ID != "burst-5" {
		t.Errorf("expected last remaining item %q, got %q", "burst-5", items[2].ID)
	}
}

// Intent: PurgeSSHSessions removes old SSH session records.
func TestPurgeSSHSessions_RemovesOldRecords(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:        "ssh-purge-1",
		User:      "root",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
		Queued:    0,
	}

	if err := store.InsertSSHSession(session); err != nil {
		t.Fatalf("InsertSSHSession: %v", err)
	}

	// Backdate the session timestamp.
	oldTime := time.Now().UTC().Add(-48 * time.Hour).Format(time.RFC3339)
	_, err := store.DB().Exec("UPDATE ssh_sessions SET timestamp = ?", oldTime)
	if err != nil {
		t.Fatalf("backdating session: %v", err)
	}

	deleted, err := store.PurgeSSHSessions(24 * time.Hour)
	if err != nil {
		t.Fatalf("PurgeSSHSessions: %v", err)
	}
	if deleted != 1 {
		t.Errorf("expected 1 deleted, got %d", deleted)
	}
}

// Intent: After Close(), operations return errors.
func TestClose_ClosesDB(t *testing.T) {
	database, err := Open(":memory:")
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	store := NewStore(database, 0)

	// Close the store.
	if err := store.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	// Operations should fail after close.
	_, err = store.GetConfig("test")
	if err == nil {
		t.Error("expected error after Close(), got nil")
	}
}

// Intent: Completed items can be reset to pending for retry via MarkTelemetryPending.
func TestMarkTelemetryPending_ResetsCompletedItems(t *testing.T) {
	store := newTestStore(t)

	if err := store.EnqueueTelemetry("retry-1", TelemetryCpuUsage, `{}`); err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	// Dequeue then mark completed.
	_, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if err := store.MarkTelemetryCompleted([]string{"retry-1"}); err != nil {
		t.Fatalf("MarkTelemetryCompleted: %v", err)
	}

	// Mark back to pending.
	if err := store.MarkTelemetryPending([]string{"retry-1"}); err != nil {
		t.Fatalf("MarkTelemetryPending: %v", err)
	}

	// Item should be dequeueable again.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry after pending: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 item after marking pending, got %d", len(items))
	}
}

// Intent: InsertSSHSessionAndEnqueue atomically inserts session, enqueues telemetry, and marks queued.
func TestInsertSSHSessionAndEnqueue_Success(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:        "ssh-tx-1",
		User:      "root",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
		Queued:    0,
	}

	err := store.InsertSSHSessionAndEnqueue(session, "telem-ssh-1", `{"user":"root"}`)
	if err != nil {
		t.Fatalf("InsertSSHSessionAndEnqueue: %v", err)
	}

	// Session should exist and be marked as queued.
	sessions, err := store.GetUnqueuedSSHSessions()
	if err != nil {
		t.Fatalf("GetUnqueuedSSHSessions: %v", err)
	}
	if len(sessions) != 0 {
		t.Errorf("expected 0 unqueued sessions (should be marked queued), got %d", len(sessions))
	}

	// Telemetry item should be enqueued.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ID != "telem-ssh-1" {
		t.Errorf("expected telemetry ID %q, got %q", "telem-ssh-1", items[0].ID)
	}
	if items[0].ItemType != TelemetrySSHSession {
		t.Errorf("expected telemetry type %d (SSHSession), got %d", TelemetrySSHSession, items[0].ItemType)
	}
	if items[0].Payload != `{"user":"root"}` {
		t.Errorf("expected payload %q, got %q", `{"user":"root"}`, items[0].Payload)
	}
}

// Intent: InsertSSHSessionAndEnqueue updates pending count atomically.
func TestInsertSSHSessionAndEnqueue_UpdatesPendingCount(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:        "ssh-tx-cnt",
		User:      "admin",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
	}

	countBefore, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry before: %v", err)
	}

	err = store.InsertSSHSessionAndEnqueue(session, "telem-cnt-1", `{}`)
	if err != nil {
		t.Fatalf("InsertSSHSessionAndEnqueue: %v", err)
	}

	countAfter, err := store.CountPendingTelemetry()
	if err != nil {
		t.Fatalf("CountPendingTelemetry after: %v", err)
	}

	if countAfter != countBefore+1 {
		t.Errorf("expected pending count to increase by 1, got before=%d after=%d", countBefore, countAfter)
	}
}

// Intent: InsertSSHSessionAndEnqueue with duplicate session ID returns error.
func TestInsertSSHSessionAndEnqueue_DuplicateSessionID(t *testing.T) {
	store := newTestStore(t)

	session := &SSHSession{
		ID:        "ssh-dup",
		User:      "root",
		Action:    "login",
		Timestamp: time.Now().UTC().Format(time.RFC3339),
	}

	err := store.InsertSSHSessionAndEnqueue(session, "telem-dup-1", `{}`)
	if err != nil {
		t.Fatalf("first InsertSSHSessionAndEnqueue: %v", err)
	}

	// Second insert with same session ID should fail and roll back.
	err = store.InsertSSHSessionAndEnqueue(session, "telem-dup-2", `{}`)
	if err == nil {
		t.Error("expected error for duplicate session ID")
	}

	// Only the first telemetry item should exist.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 telemetry item (rollback should prevent second), got %d", len(items))
	}
}

// Intent: DequeueTelemetryByTypes with nil types returns all pending items.
func TestDequeueTelemetryByTypes_NilTypes(t *testing.T) {
	store := newTestStore(t)

	if err := store.EnqueueTelemetry("nil-1", TelemetryCpuUsage, `{}`); err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}
	if err := store.EnqueueTelemetry("nil-2", TelemetryMemoryInfo, `{}`); err != nil {
		t.Fatalf("EnqueueTelemetry: %v", err)
	}

	items, err := store.DequeueTelemetryByTypes(nil, 10)
	if err != nil {
		t.Fatalf("DequeueTelemetryByTypes(nil): %v", err)
	}
	if len(items) != 2 {
		t.Errorf("expected 2 items with nil types, got %d", len(items))
	}
}

// --- Signing key tests ---

// Intent: UpsertSigningKeys stores keys that can be retrieved by GetSigningKey.
func TestUpsertSigningKeys_AndGetSigningKey_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	keys := []TrustedKey{
		{KeyID: 1, UserID: 10, PublicKey: []byte("key-1-bytes")},
		{KeyID: 2, UserID: 20, PublicKey: []byte("key-2-bytes")},
	}

	err := store.UpsertSigningKeys(keys)
	if err != nil {
		t.Fatalf("UpsertSigningKeys: %v", err)
	}

	k, err := store.GetSigningKey(1)
	if err != nil {
		t.Fatalf("GetSigningKey(1): %v", err)
	}
	if k.KeyID != 1 {
		t.Errorf("expected KeyID=1, got %d", k.KeyID)
	}
	if k.UserID != 10 {
		t.Errorf("expected UserID=10, got %d", k.UserID)
	}
	if string(k.PublicKey) != "key-1-bytes" {
		t.Errorf("expected PublicKey=%q, got %q", "key-1-bytes", k.PublicKey)
	}

	k2, err := store.GetSigningKey(2)
	if err != nil {
		t.Fatalf("GetSigningKey(2): %v", err)
	}
	if k2.UserID != 20 {
		t.Errorf("expected UserID=20, got %d", k2.UserID)
	}
}

// Intent: UpsertSigningKeys replaces all previous keys (not append).
func TestUpsertSigningKeys_ReplacesExistingKeys(t *testing.T) {
	store := newTestStore(t)

	first := []TrustedKey{
		{KeyID: 1, UserID: 10, PublicKey: []byte("old-key")},
		{KeyID: 2, UserID: 20, PublicKey: []byte("old-key-2")},
	}
	if err := store.UpsertSigningKeys(first); err != nil {
		t.Fatalf("first UpsertSigningKeys: %v", err)
	}

	// Upsert with only one key — key 2 should be gone.
	second := []TrustedKey{
		{KeyID: 3, UserID: 30, PublicKey: []byte("new-key")},
	}
	if err := store.UpsertSigningKeys(second); err != nil {
		t.Fatalf("second UpsertSigningKeys: %v", err)
	}

	// Key 1 should no longer exist.
	_, err := store.GetSigningKey(1)
	if err == nil {
		t.Error("expected error for deleted key 1, got nil")
	}

	// Key 3 should exist.
	k, err := store.GetSigningKey(3)
	if err != nil {
		t.Fatalf("GetSigningKey(3): %v", err)
	}
	if k.UserID != 30 {
		t.Errorf("expected UserID=30, got %d", k.UserID)
	}
}

// Intent: GetSigningKey returns error for nonexistent key.
func TestGetSigningKey_NotFound(t *testing.T) {
	store := newTestStore(t)

	_, err := store.GetSigningKey(999)
	if err == nil {
		t.Error("expected error for nonexistent key, got nil")
	}
}

// --- Nonce tests ---

// Intent: RecordNonce stores nonce that IsNonceUsed can find.
func TestRecordNonce_AndIsNonceUsed_RoundTrip(t *testing.T) {
	store := newTestStore(t)

	used, err := store.IsNonceUsed("nonce-abc")
	if err != nil {
		t.Fatalf("IsNonceUsed: %v", err)
	}
	if used {
		t.Error("expected nonce to not be used initially")
	}

	err = store.RecordNonce("nonce-abc")
	if err != nil {
		t.Fatalf("RecordNonce: %v", err)
	}

	used, err = store.IsNonceUsed("nonce-abc")
	if err != nil {
		t.Fatalf("IsNonceUsed after record: %v", err)
	}
	if used == false {
		t.Error("expected nonce to be used after RecordNonce")
	}
}

// Intent: RecordNonce is idempotent (INSERT OR IGNORE).
func TestRecordNonce_Idempotent(t *testing.T) {
	store := newTestStore(t)

	err := store.RecordNonce("nonce-dup")
	if err != nil {
		t.Fatalf("first RecordNonce: %v", err)
	}

	err = store.RecordNonce("nonce-dup")
	if err != nil {
		t.Fatalf("second RecordNonce should not fail: %v", err)
	}
}

// Intent: PurgeNonces removes old nonces but keeps recent ones.
func TestPurgeNonces_RemovesOld(t *testing.T) {
	store := newTestStore(t)

	err := store.RecordNonce("old-nonce")
	if err != nil {
		t.Fatalf("RecordNonce: %v", err)
	}

	// Backdate the nonce.
	oldTime := time.Now().UTC().Add(-48 * time.Hour).Format(time.RFC3339)
	_, err = store.DB().Exec("UPDATE command_nonces SET executed_at = ?", oldTime)
	if err != nil {
		t.Fatalf("backdating nonce: %v", err)
	}

	// Add a recent nonce.
	err = store.RecordNonce("recent-nonce")
	if err != nil {
		t.Fatalf("RecordNonce recent: %v", err)
	}

	deleted, err := store.PurgeNonces(24 * time.Hour)
	if err != nil {
		t.Fatalf("PurgeNonces: %v", err)
	}
	if deleted != 1 {
		t.Errorf("expected 1 deleted, got %d", deleted)
	}

	// Old nonce should be gone.
	used, err := store.IsNonceUsed("old-nonce")
	if err != nil {
		t.Fatalf("IsNonceUsed: %v", err)
	}
	if used {
		t.Error("expected old nonce to be purged")
	}

	// Recent nonce should remain.
	used, err = store.IsNonceUsed("recent-nonce")
	if err != nil {
		t.Fatalf("IsNonceUsed: %v", err)
	}
	if used == false {
		t.Error("expected recent nonce to still exist")
	}
}

// Intent: RecordNonce must reject inserts when the nonce table reaches maxNonceTableSize,
// preventing a compromised server from exhausting disk space via unbounded nonce growth.
func TestRecordNonce_TableSizeGuard(t *testing.T) {
	store := newTestStore(t)

	// Fill the nonce table to the maximum capacity of 10000.
	for i := 0; i < 10000; i++ {
		nonce := fmt.Sprintf("nonce-%d", i)
		err := store.RecordNonce(nonce)
		if err != nil {
			t.Fatalf("RecordNonce %d: %v", i, err)
		}
	}

	// The 10001st insert must be rejected with a capacity error.
	err := store.RecordNonce("nonce-overflow")
	if err == nil {
		t.Fatal("expected error when nonce table is at capacity, got nil")
	}

	errMsg := err.Error()
	if strings.Contains(errMsg, "nonce table at capacity") == false {
		t.Errorf("expected error containing %q, got %q", "nonce table at capacity", errMsg)
	}

	// Verify the overflow nonce was not actually stored.
	used, err := store.IsNonceUsed("nonce-overflow")
	if err != nil {
		t.Fatalf("IsNonceUsed after overflow: %v", err)
	}
	if used {
		t.Error("expected overflow nonce to not be stored")
	}
}

// Intent: When the queue has maxQueueSize=3 and a 4th item is enqueued, the oldest
// pending item is evicted so only the 3 newest items remain.
func TestEnqueueTelemetry_EvictsOldestWhenAtCapacity(t *testing.T) {
	database, err := Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	store := NewStore(database, 3)

	// Enqueue 3 items to fill the queue to capacity.
	for i := 1; i <= 3; i++ {
		id := fmt.Sprintf("evict-%d", i)
		enqErr := store.EnqueueTelemetry(id, TelemetryCpuUsage, fmt.Sprintf(`{"seq":%d}`, i))
		if enqErr != nil {
			t.Fatalf("EnqueueTelemetry %d: %v", i, enqErr)
		}
	}

	// Enqueue a 4th item, which should evict the oldest (evict-1).
	enqErr := store.EnqueueTelemetry("evict-4", TelemetryCpuUsage, `{"seq":4}`)
	if enqErr != nil {
		t.Fatalf("EnqueueTelemetry 4th item: %v", enqErr)
	}

	// Verify the pending count is still 3 (not 4).
	count, countErr := store.CountPendingTelemetry()
	if countErr != nil {
		t.Fatalf("CountPendingTelemetry: %v", countErr)
	}
	if count != 3 {
		t.Errorf("expected 3 pending items after eviction, got %d", count)
	}

	// Dequeue all items and verify exactly which ones remain.
	items, deqErr := store.DequeueTelemetry(10)
	if deqErr != nil {
		t.Fatalf("DequeueTelemetry: %v", deqErr)
	}
	if len(items) != 3 {
		t.Fatalf("expected 3 items after eviction, got %d", len(items))
	}

	// The oldest item (evict-1) should have been evicted.
	// Remaining items should be evict-2, evict-3, evict-4 in FIFO order.
	expectedIDs := []string{"evict-2", "evict-3", "evict-4"}
	for i, expected := range expectedIDs {
		if items[i].ID != expected {
			t.Errorf("expected item[%d].ID=%q, got %q", i, expected, items[i].ID)
		}
	}
}
