// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/framlux/vord/internal/crypto"
)

// --- Mock implementations for Processor tests ---

// mockNonceStore provides configurable behavior for NonceStore methods.
type mockNonceStore struct {
	isUsedFn     func(nonce string) (bool, error)
	recordFn     func(nonce string) error
	getKeyFn     func(keyID int32) (*SigningKey, error)
	recordedNonces []string
}

func (m *mockNonceStore) IsNonceUsed(nonce string) (bool, error) {
	if m.isUsedFn != nil {
		return m.isUsedFn(nonce)
	}

	return false, nil
}

func (m *mockNonceStore) RecordNonce(nonce string) error {
	m.recordedNonces = append(m.recordedNonces, nonce)
	if m.recordFn != nil {
		return m.recordFn(nonce)
	}

	return nil
}

func (m *mockNonceStore) GetSigningKey(keyID int32) (*SigningKey, error) {
	if m.getKeyFn != nil {
		return m.getKeyFn(keyID)
	}

	return nil, fmt.Errorf("key not found")
}

// ackCall records one call to Acknowledge.
type ackCall struct {
	CommandID  string
	MachineID  int64
	Success    bool
	ExitCode   int32
	Stdout     string
	Stderr     string
	Message    string
	ResultType int32
}

// mockAcknowledger records all Acknowledge calls for assertion.
type mockAcknowledger struct {
	calls []ackCall
}

func (m *mockAcknowledger) Acknowledge(ctx context.Context, commandID string, machineID int64, success bool, exitCode int32, stdout string, stderr string, message string, resultType int32) {
	m.calls = append(m.calls, ackCall{
		CommandID:  commandID,
		MachineID:  machineID,
		Success:    success,
		ExitCode:   exitCode,
		Stdout:     stdout,
		Stderr:     stderr,
		Message:    message,
		ResultType: resultType,
	})
}

// mockExecutor returns a configurable Result for any command.
type mockExecutor struct {
	result Result
}

func (m *mockExecutor) Execute(ctx context.Context, params map[string]string) Result {
	return m.result
}

// newTestHandler creates a Handler with a mock executor for the given command type.
func newTestHandler(cmdType CommandType, result Result) *Handler {
	h := &Handler{
		executors: make(map[CommandType]Executor),
	}
	h.executors[cmdType] = &mockExecutor{result: result}

	return h
}

// newTestHandlerAllTypes creates a Handler with mock executors for all valid command types.
func newTestHandlerAllTypes(result Result) *Handler {
	h := &Handler{
		executors: make(map[CommandType]Executor),
	}
	for ct := range validCommandTypes {
		h.executors[ct] = &mockExecutor{result: result}
	}

	return h
}

// testKeyPair generates a fresh Ed25519 key pair for test signing.
func testKeyPair(t *testing.T) (ed25519.PublicKey, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("failed to generate Ed25519 key pair: %v", err)
	}

	return pub, priv
}

// validPendingCommand creates a properly signed PendingCommand for testing.
// The command passes all validation steps when used with the returned signing key.
func validPendingCommand(t *testing.T, cmdType CommandType, tenantID int32, machineID int64, priv ed25519.PrivateKey, pub ed25519.PublicKey, keyID int32) PendingCommand {
	t.Helper()

	now := time.Now().UTC()
	cmd := PendingCommand{
		ID:           "cmd-test-001",
		Type:         string(cmdType),
		Params:       map[string]string{},
		SigningKeyID: keyID,
		Timestamp:    now.Format(time.RFC3339),
		ExpiresAt:    now.Add(10 * time.Minute).Format(time.RFC3339),
		Nonce:        "unique-nonce-12345",
		UserID:       42,
		TenantID:     tenantID,
		MachineID:    machineID,
	}

	// Build canonical payload and sign it.
	payload, err := crypto.BuildCanonicalPayload(crypto.CanonicalPayload{
		CommandID:   cmd.ID,
		CommandType: cmd.Type,
		ExpiresAt:   cmd.ExpiresAt,
		MachineID:   cmd.MachineID,
		Nonce:       cmd.Nonce,
		Params:      cmd.Params,
		TenantID:    cmd.TenantID,
		Timestamp:   cmd.Timestamp,
		UserID:      cmd.UserID,
	})
	if err != nil {
		t.Fatalf("failed to build canonical payload: %v", err)
	}

	cmd.CanonicalPayload = string(payload)
	cmd.Signature = ed25519.Sign(priv, payload)

	return cmd
}

// --- Processor tests ---

// Intent: A valid non-destructive command passes all validation and is executed with Completed result.
func TestProcess_ValidNonDestructiveCommand(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "updates checked"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeCompleted {
		t.Errorf("expected ResultTypeCompleted (%d), got %d", ResultTypeCompleted, ack.calls[0].ResultType)
	}
	if ack.calls[0].Success == false {
		t.Error("expected Success=true for valid command")
	}
	if ack.calls[0].Message != "updates checked" {
		t.Errorf("expected message %q, got %q", "updates checked", ack.calls[0].Message)
	}
	if ack.calls[0].CommandID != cmd.ID {
		t.Errorf("expected command ID %q, got %q", cmd.ID, ack.calls[0].CommandID)
	}
}

// Intent: Command with mismatched tenant ID is rejected before any crypto validation occurs.
func TestProcess_TenantMismatch(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	// Command has tenant 100, agent has tenant 999.
	cmd := validPendingCommand(t, CommandCheckUpdate, 100, 200, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, 999, 200)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "tenant mismatch") == false {
		t.Errorf("expected 'tenant mismatch' in message, got %q", ack.calls[0].Message)
	}
	if ack.calls[0].Success {
		t.Error("expected Success=false for tenant mismatch")
	}
}

// Intent: Command with mismatched machine ID is rejected before any crypto validation occurs.
func TestProcess_MachineMismatch(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	// Command has machine 200, agent has machine 888.
	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, 200, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, 888)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "machine mismatch") == false {
		t.Errorf("expected 'machine mismatch' in message, got %q", ack.calls[0].Message)
	}
	if ack.calls[0].Success {
		t.Error("expected Success=false for machine mismatch")
	}
}

// Intent: Command with unrecognized type string is rejected immediately.
func TestProcess_UnknownCommandType(t *testing.T) {
	store := &mockNonceStore{}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := PendingCommand{
		ID:        "cmd-unknown-type",
		Type:      "drop_database",
		TenantID:  100,
		MachineID: 200,
	}

	processor.Process(context.Background(), cmd, 100, 200)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "unknown command type") == false {
		t.Errorf("expected 'unknown command type' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Command referencing a signing key that the store does not recognize is rejected.
func TestProcess_UnknownSigningKey(t *testing.T) {
	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return nil, fmt.Errorf("key not found: %d", keyID)
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := PendingCommand{
		ID:           "cmd-bad-key",
		Type:         string(CommandCheckUpdate),
		TenantID:     100,
		MachineID:    200,
		SigningKeyID: 999,
	}

	processor.Process(context.Background(), cmd, 100, 200)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "unknown signing key") == false {
		t.Errorf("expected 'unknown signing key' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Command with a valid key but tampered signature is rejected.
func TestProcess_InvalidSignature(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)

	// Tamper with the signature by flipping a byte.
	cmd.Signature[0] ^= 0xFF

	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "invalid signature") == false {
		t.Errorf("expected 'invalid signature' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Command whose server-supplied canonical payload differs from the locally rebuilt one is rejected.
func TestProcess_CanonicalPayloadMismatch(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)

	// Tamper with the canonical payload to create a mismatch with the rebuilt payload.
	cmd.CanonicalPayload = `{"tampered": true}`

	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "canonical payload mismatch") == false {
		t.Errorf("expected 'canonical payload mismatch' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Command whose ExpiresAt is in the past is rejected as expired.
func TestProcess_ExpiredCommand(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	// Build an expired command: timestamp and expiry both in the past.
	now := time.Now().UTC()
	cmd := PendingCommand{
		ID:           "cmd-expired",
		Type:         string(CommandCheckUpdate),
		Params:       map[string]string{},
		SigningKeyID: int32KeyID,
		Timestamp:    now.Add(-10 * time.Minute).Format(time.RFC3339),
		ExpiresAt:    now.Add(-1 * time.Minute).Format(time.RFC3339),
		Nonce:        "expired-nonce",
		UserID:       42,
		TenantID:     int32TenantID,
		MachineID:    int64MachineID,
	}

	// Build canonical payload and sign with real key.
	payload, err := crypto.BuildCanonicalPayload(crypto.CanonicalPayload{
		CommandID:   cmd.ID,
		CommandType: cmd.Type,
		ExpiresAt:   cmd.ExpiresAt,
		MachineID:   cmd.MachineID,
		Nonce:       cmd.Nonce,
		Params:      cmd.Params,
		TenantID:    cmd.TenantID,
		Timestamp:   cmd.Timestamp,
		UserID:      cmd.UserID,
	})
	if err != nil {
		t.Fatalf("failed to build canonical payload: %v", err)
	}

	cmd.CanonicalPayload = string(payload)
	cmd.Signature = ed25519.Sign(priv, payload)

	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	// The timestamp validation error message contains "expired" or "too old".
	if strings.Contains(ack.calls[0].Message, "expired") == false &&
		strings.Contains(ack.calls[0].Message, "too old") == false {
		t.Errorf("expected expiration-related message, got %q", ack.calls[0].Message)
	}
}

// Intent: Command with a previously-used nonce is rejected as a replay attack.
func TestProcess_ReusedNonce(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
		isUsedFn: func(nonce string) (bool, error) {
			return true, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "nonce already used") == false {
		t.Errorf("expected 'nonce already used' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Failure to record the nonce in the store rejects the command to prevent replay.
func TestProcess_NonceRecordingFailure(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
		recordFn: func(nonce string) error {
			return fmt.Errorf("database write failed")
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "nonce recording failed") == false {
		t.Errorf("expected 'nonce recording failed' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: Destructive command first ACKs with Initiated, then after delay executes and ACKs with Completed.
// Uses context cancellation to skip the delay in tests.
func TestProcess_DestructiveCommand_SkipsDelayOnContextCancel(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "reboot initiated"})

	// Use a long delay so the context cancellation triggers the skip path.
	processor := NewProcessor(handler, store, ack, 10*time.Minute)

	cmd := validPendingCommand(t, CommandReboot, int32TenantID, int64MachineID, priv, pub, int32KeyID)

	ctx, cancel := context.WithCancel(context.Background())

	doneCh := make(chan struct{})
	go func() {
		processor.Process(ctx, cmd, int32TenantID, int64MachineID)
		close(doneCh)
	}()

	// Allow the goroutine to reach the select statement, then cancel.
	time.Sleep(50 * time.Millisecond)
	cancel()

	select {
	case <-doneCh:
		// Process returned after context cancellation.
	case <-time.After(5 * time.Second):
		t.Fatal("Process did not return after context cancellation")
	}

	// Only the Initiated ACK should have been sent; the command should not execute.
	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call (Initiated only), got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeInitiated {
		t.Errorf("expected ResultTypeInitiated (%d), got %d", ResultTypeInitiated, ack.calls[0].ResultType)
	}
	if ack.calls[0].Success == false {
		t.Error("expected Success=true for Initiated ack")
	}
}

// Intent: Destructive command executes after delay completes and sends both Initiated and Completed ACKs.
func TestProcess_DestructiveCommand_ExecutesAfterDelay(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "reboot initiated"})

	// Use a minimal delay so the test completes quickly.
	processor := NewProcessor(handler, store, ack, 1*time.Millisecond)

	cmd := validPendingCommand(t, CommandReboot, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 2 {
		t.Fatalf("expected 2 ack calls (Initiated + Completed), got %d", len(ack.calls))
	}

	// First ACK: Initiated.
	if ack.calls[0].ResultType != ResultTypeInitiated {
		t.Errorf("first ack: expected ResultTypeInitiated (%d), got %d", ResultTypeInitiated, ack.calls[0].ResultType)
	}
	if ack.calls[0].Success == false {
		t.Error("first ack: expected Success=true")
	}

	// Second ACK: Completed after execution.
	if ack.calls[1].ResultType != ResultTypeCompleted {
		t.Errorf("second ack: expected ResultTypeCompleted (%d), got %d", ResultTypeCompleted, ack.calls[1].ResultType)
	}
	if ack.calls[1].Success == false {
		t.Error("second ack: expected Success=true")
	}
	if ack.calls[1].Message != "reboot initiated" {
		t.Errorf("second ack: expected message %q, got %q", "reboot initiated", ack.calls[1].Message)
	}
}

// Intent: A non-destructive command that fails execution returns ResultTypeFailed.
func TestProcess_FailedNonDestructiveCommand_ReturnsFailed(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{
		Success: false,
		Message: "process not found",
		Error:   fmt.Errorf("no such process"),
	})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandKillProcess, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeFailed {
		t.Errorf("expected ResultTypeFailed (%d), got %d", ResultTypeFailed, ack.calls[0].ResultType)
	}
	if ack.calls[0].Success {
		t.Error("expected Success=false for failed command")
	}
	if ack.calls[0].Message != "process not found" {
		t.Errorf("expected message %q, got %q", "process not found", ack.calls[0].Message)
	}
}

// Intent: A destructive command that fails execution returns ResultTypeFailed after delay.
func TestProcess_FailedDestructiveCommand_ReturnsFailed(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{
		Success: false,
		Message: "permission denied",
		Error:   fmt.Errorf("shutdown failed"),
	})

	// Use a minimal delay so the test completes quickly.
	processor := NewProcessor(handler, store, ack, 1*time.Millisecond)

	cmd := validPendingCommand(t, CommandReboot, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 2 {
		t.Fatalf("expected 2 ack calls (Initiated + Failed), got %d", len(ack.calls))
	}

	// First ACK: Initiated (sent before execution attempt).
	if ack.calls[0].ResultType != ResultTypeInitiated {
		t.Errorf("first ack: expected ResultTypeInitiated (%d), got %d", ResultTypeInitiated, ack.calls[0].ResultType)
	}

	// Second ACK: Failed after execution.
	if ack.calls[1].ResultType != ResultTypeFailed {
		t.Errorf("second ack: expected ResultTypeFailed (%d), got %d", ResultTypeFailed, ack.calls[1].ResultType)
	}
	if ack.calls[1].Success {
		t.Error("second ack: expected Success=false for failed command")
	}
	if ack.calls[1].Message != "permission denied" {
		t.Errorf("second ack: expected message %q, got %q", "permission denied", ack.calls[1].Message)
	}
}

// Intent: Nonce check error (store read failure) is rejected, distinct from "nonce already used".
func TestProcess_NonceCheckError(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
		isUsedFn: func(nonce string) (bool, error) {
			return false, fmt.Errorf("database connection lost")
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "nonce check error") == false {
		t.Errorf("expected 'nonce check error' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: The nonce is recorded in the store after successful validation.
func TestProcess_NonceIsRecorded(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(store.recordedNonces) != 1 {
		t.Fatalf("expected 1 recorded nonce, got %d", len(store.recordedNonces))
	}
	if store.recordedNonces[0] != cmd.Nonce {
		t.Errorf("expected recorded nonce %q, got %q", cmd.Nonce, store.recordedNonces[0])
	}
}

// Intent: ACK always uses the agent's machine ID, not the command's machine ID field.
func TestProcess_AckUsesAgentMachineID(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) < 1 {
		t.Fatal("expected at least 1 ack call")
	}
	if ack.calls[0].MachineID != int64MachineID {
		t.Errorf("expected ack machine ID %d, got %d", int64MachineID, ack.calls[0].MachineID)
	}
}

// Intent: Command signed with a different key than the one stored is rejected.
func TestProcess_SignedWithWrongKey(t *testing.T) {
	storedPub, _ := testKeyPair(t)
	_, signingPriv := testKeyPair(t)
	signingPub := signingPriv.Public().(ed25519.PublicKey)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	// Store has one key, but the command is signed with a different key.
	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: storedPub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, signingPriv, signingPub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "invalid signature") == false {
		t.Errorf("expected 'invalid signature' in message, got %q", ack.calls[0].Message)
	}
}

// Intent: A command whose UserID does not match the UserID bound to the signing key
// is rejected even when the Ed25519 signature is valid. This prevents a signing key
// issued to one user from being used to authorize commands attributed to another user.
func TestProcess_SignedUserIDMismatch(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	// The stored key is bound to user 7, but the command (validPendingCommand) carries UserID 42.
	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 7, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	// validPendingCommand signs the payload correctly with UserID 42, so the signature is valid.
	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	if cmd.UserID == 7 {
		t.Fatal("test setup invalid: command UserID must differ from key UserID")
	}

	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "user/key mismatch") == false {
		t.Errorf("expected 'user/key mismatch' in message, got %q", ack.calls[0].Message)
	}
	if ack.calls[0].Success {
		t.Error("expected Success=false for user/key mismatch")
	}

	// The command must not execute: no nonce should be recorded.
	if len(store.recordedNonces) != 0 {
		t.Errorf("expected no nonce recorded on user/key mismatch, got %d", len(store.recordedNonces))
	}
}

// Intent: A command whose UserID matches the UserID bound to the signing key is accepted.
func TestProcess_SignedUserIDMatch_Accepted(t *testing.T) {
	pub, priv := testKeyPair(t)
	int32KeyID := int32(1)
	int32TenantID := int32(100)
	int64MachineID := int64(200)

	// The stored key is bound to user 42, matching validPendingCommand's UserID.
	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: int32KeyID, UserID: 42, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := validPendingCommand(t, CommandCheckUpdate, int32TenantID, int64MachineID, priv, pub, int32KeyID)
	processor.Process(context.Background(), cmd, int32TenantID, int64MachineID)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeCompleted {
		t.Errorf("expected ResultTypeCompleted (%d), got %d", ResultTypeCompleted, ack.calls[0].ResultType)
	}
}

// Intent: Empty command type string is rejected as unknown.
func TestProcess_EmptyCommandType(t *testing.T) {
	store := &mockNonceStore{}
	ack := &mockAcknowledger{}
	handler := newTestHandlerAllTypes(Result{Success: true, Message: "ok"})
	processor := NewProcessor(handler, store, ack, 10*time.Second)

	cmd := PendingCommand{
		ID:        "cmd-empty-type",
		Type:      "",
		TenantID:  100,
		MachineID: 200,
	}

	processor.Process(context.Background(), cmd, 100, 200)

	if len(ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d", len(ack.calls))
	}
	if ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, ack.calls[0].ResultType)
	}
	if strings.Contains(ack.calls[0].Message, "unknown command type") == false {
		t.Errorf("expected 'unknown command type' in message, got %q", ack.calls[0].Message)
	}
}
