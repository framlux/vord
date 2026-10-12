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
	isUsedFn       func(nonce string) (bool, error)
	recordFn       func(nonce string) error
	getKeyFn       func(keyID int32) (*SigningKey, error)
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

// Intent: The signature covers the exact bytes the server delivers. A payload that was altered after signing
// is an invalid signature, and that is decided before the payload is parsed, so even unparseable bytes are
// reported as a signature failure rather than as a payload error.
func TestProcess_TamperedCanonicalPayload_InvalidSignature(t *testing.T) {
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

	// The bytes no longer match what was signed, and are not even a valid payload.
	cmd.CanonicalPayload = `{"tampered": true}`

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

// --- Verification against the exact signed payload ---

// signedTestNow is the instant the processor treats as the present in the signed payload tests.
var signedTestNow = time.Date(2026, time.October, 11, 12, 1, 0, 0, time.UTC)

const (
	signedTestKeyID    int32 = 1
	signedTestTenantID int32 = 100
	signedTestMachine  int64 = 200
	signedTestUserID   int32 = 42
	signedTestNonce          = "0123456789abcdef0123456789abcdef"
	signedTestCommand        = "11111111-2222-3333-4444-555555555555"
)

// webSignedCheckUpdates is the exact string the web client signs for a command with no parameters: sorted
// keys, no whitespace, no params key and Date.toISOString() timestamps.
const webSignedCheckUpdates = `{"command_id":"11111111-2222-3333-4444-555555555555","command_type":"check_updates","expires_at":"2026-10-11T12:10:00.000Z","machine_id":200,"nonce":"0123456789abcdef0123456789abcdef","tenant_id":100,"timestamp":"2026-10-11T12:00:00.000Z","user_id":42}`

// executedCommand records one execution reaching an executor.
type executedCommand struct {
	Type   CommandType
	Params map[string]string
}

// recordingExecutor appends every execution it receives to a shared log.
type recordingExecutor struct {
	cmdType CommandType
	log     *[]executedCommand
	result  Result
}

func (r *recordingExecutor) Execute(ctx context.Context, params map[string]string) Result {
	*r.log = append(*r.log, executedCommand{Type: r.cmdType, Params: params})

	return r.result
}

// signedFixture wires a processor to a trusted key, a recording handler and a fixed clock.
type signedFixture struct {
	priv      ed25519.PrivateKey
	store     *mockNonceStore
	ack       *mockAcknowledger
	executed  *[]executedCommand
	processor *Processor
}

func newSignedFixture(t *testing.T) *signedFixture {
	t.Helper()

	pub, priv := testKeyPair(t)
	executed := &[]executedCommand{}

	handler := &Handler{executors: make(map[CommandType]Executor)}
	for ct := range validCommandTypes {
		handler.executors[ct] = &recordingExecutor{cmdType: ct, log: executed, result: Result{Success: true, Message: "executed"}}
	}

	store := &mockNonceStore{
		getKeyFn: func(keyID int32) (*SigningKey, error) {
			return &SigningKey{KeyID: signedTestKeyID, UserID: signedTestUserID, PublicKey: pub}, nil
		},
	}
	ack := &mockAcknowledger{}
	processor := NewProcessor(handler, store, ack, time.Millisecond)
	processor.now = func() time.Time { return signedTestNow }

	return &signedFixture{priv: priv, store: store, ack: ack, executed: executed, processor: processor}
}

// deliver builds the command the server hands the agent for a signed payload: the server stores the
// timestamps as DateTimeOffset and re-emits them in the .NET round-trip format, with nil params.
func (f *signedFixture) deliver(signed string) PendingCommand {
	return PendingCommand{
		ID:               signedTestCommand,
		Type:             string(CommandCheckUpdate),
		Params:           nil,
		CanonicalPayload: signed,
		Signature:        ed25519.Sign(f.priv, []byte(signed)),
		SigningKeyID:     signedTestKeyID,
		Timestamp:        "2026-10-11T12:00:00.0000000+00:00",
		ExpiresAt:        "2026-10-11T12:10:00.0000000+00:00",
		Nonce:            signedTestNonce,
		UserID:           signedTestUserID,
		TenantID:         signedTestTenantID,
		MachineID:        signedTestMachine,
	}
}

func (f *signedFixture) process(cmd PendingCommand) {
	f.processor.Process(context.Background(), cmd, signedTestTenantID, signedTestMachine)
}

// requireRejected fails unless exactly one rejection containing the message was sent, the nonce stayed
// unused and nothing reached an executor.
func (f *signedFixture) requireRejected(t *testing.T, wantMessage string) {
	t.Helper()

	if len(f.ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d: %+v", len(f.ack.calls), f.ack.calls)
	}
	if f.ack.calls[0].ResultType != ResultTypeRejected {
		t.Errorf("expected ResultTypeRejected (%d), got %d", ResultTypeRejected, f.ack.calls[0].ResultType)
	}
	if f.ack.calls[0].Success {
		t.Error("expected Success=false for a rejected command")
	}
	if strings.Contains(f.ack.calls[0].Message, wantMessage) == false {
		t.Errorf("expected %q in message, got %q", wantMessage, f.ack.calls[0].Message)
	}
	if len(f.store.recordedNonces) != 0 {
		t.Errorf("a rejected command must not record its nonce, recorded %v", f.store.recordedNonces)
	}
	if len(*f.executed) != 0 {
		t.Errorf("a rejected command must not execute, executed %+v", *f.executed)
	}
}

// Intent: The command the web client issues verifies and runs after the server's round trip. The web signs
// no params key and a ".000Z" timestamp; the server delivers ".0000000+00:00" timestamps and nil params. The
// signature is over the signed bytes, the delivered fields bind to them as instants, and the command executes.
func TestProcess_WebSignedPayloadAfterServerRoundTrip_Verifies(t *testing.T) {
	f := newSignedFixture(t)

	f.process(f.deliver(webSignedCheckUpdates))

	if len(f.ack.calls) != 1 {
		t.Fatalf("expected 1 ack call, got %d: %+v", len(f.ack.calls), f.ack.calls)
	}
	if f.ack.calls[0].ResultType != ResultTypeCompleted {
		t.Errorf("expected ResultTypeCompleted (%d), got %d (%q)", ResultTypeCompleted, f.ack.calls[0].ResultType, f.ack.calls[0].Message)
	}
	if f.ack.calls[0].Success == false {
		t.Error("expected Success=true")
	}
	if len(*f.executed) != 1 {
		t.Fatalf("expected 1 execution, got %d", len(*f.executed))
	}
	if (*f.executed)[0].Type != CommandCheckUpdate {
		t.Errorf("executed %q, want %q", (*f.executed)[0].Type, CommandCheckUpdate)
	}
	if len((*f.executed)[0].Params) != 0 {
		t.Errorf("expected no params, got %v", (*f.executed)[0].Params)
	}
	if len(f.store.recordedNonces) != 1 || f.store.recordedNonces[0] != signedTestNonce {
		t.Errorf("expected the signed nonce recorded, got %v", f.store.recordedNonces)
	}
}

// Intent: An absent signed params key and an empty delivered map describe the same command.
func TestProcess_SignedPayloadWithoutParams_DeliveredEmptyMap_Verifies(t *testing.T) {
	f := newSignedFixture(t)

	cmd := f.deliver(webSignedCheckUpdates)
	cmd.Params = map[string]string{}
	f.process(cmd)

	if len(f.ack.calls) != 1 || f.ack.calls[0].ResultType != ResultTypeCompleted {
		t.Fatalf("expected a completed command, got %+v", f.ack.calls)
	}
}

// Intent: Signed params are what executes. The delivered params are equal to them once bound, and the
// executor receives exactly the signed key/value pairs.
func TestProcess_SignedParams_AreExecuted(t *testing.T) {
	f := newSignedFixture(t)

	signed := strings.Replace(
		strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"kill_process"`, 1),
		`"tenant_id"`, `"params":{"pid":"1234","signal":"TERM"},"tenant_id"`, 1)

	cmd := f.deliver(signed)
	cmd.Type = string(CommandKillProcess)
	cmd.Params = map[string]string{"pid": "1234", "signal": "TERM"}
	f.process(cmd)

	if len(f.ack.calls) != 1 || f.ack.calls[0].ResultType != ResultTypeCompleted {
		t.Fatalf("expected a completed command, got %+v", f.ack.calls)
	}
	if len(*f.executed) != 1 {
		t.Fatalf("expected 1 execution, got %d", len(*f.executed))
	}
	got := (*f.executed)[0]
	if got.Type != CommandKillProcess {
		t.Errorf("executed %q, want %q", got.Type, CommandKillProcess)
	}
	if len(got.Params) != 2 || got.Params["pid"] != "1234" || got.Params["signal"] != "TERM" {
		t.Errorf("executed with params %v, want the signed pid and signal", got.Params)
	}
}

// Intent: Every signed field must agree with the delivered command. A validly signed payload that describes a
// different machine, type, nonce, params, command, user, tenant or instant is a mismatch and never executes.
func TestProcess_SignedPayloadDiffersFromDeliveredCommand_Mismatch(t *testing.T) {
	cases := []struct {
		name   string
		signed string
		mutate func(*PendingCommand)
	}{
		{
			name:   "machine",
			signed: strings.Replace(webSignedCheckUpdates, `"machine_id":200`, `"machine_id":201`, 1),
		},
		{
			name:   "command type",
			signed: strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"reboot"`, 1),
		},
		{
			name:   "nonce",
			signed: strings.Replace(webSignedCheckUpdates, `"nonce":"0123456789abcdef0123456789abcdef"`, `"nonce":"ffffffffffffffffffffffffffffffff"`, 1),
		},
		{
			name:   "params signed but delivered nil",
			signed: strings.Replace(webSignedCheckUpdates, `"tenant_id"`, `"params":{"a":"b"},"tenant_id"`, 1),
		},
		{
			name:   "params delivered but never signed",
			signed: webSignedCheckUpdates,
			mutate: func(c *PendingCommand) { c.Params = map[string]string{"a": "b"} },
		},
		{
			name:   "params value",
			signed: strings.Replace(webSignedCheckUpdates, `"tenant_id"`, `"params":{"a":"b"},"tenant_id"`, 1),
			mutate: func(c *PendingCommand) { c.Params = map[string]string{"a": "c"} },
		},
		{
			name:   "timestamp instant",
			signed: strings.Replace(webSignedCheckUpdates, `"timestamp":"2026-10-11T12:00:00.000Z"`, `"timestamp":"2026-10-11T12:00:01.000Z"`, 1),
		},
		{
			name:   "timestamp same wall clock in another offset",
			signed: strings.Replace(webSignedCheckUpdates, `"timestamp":"2026-10-11T12:00:00.000Z"`, `"timestamp":"2026-10-11T12:00:00.000+02:00"`, 1),
		},
		{
			name:   "expires_at instant",
			signed: strings.Replace(webSignedCheckUpdates, `"expires_at":"2026-10-11T12:10:00.000Z"`, `"expires_at":"2026-10-11T12:20:00.000Z"`, 1),
		},
		{
			name:   "command id",
			signed: strings.Replace(webSignedCheckUpdates, signedTestCommand, "99999999-2222-3333-4444-555555555555", 1),
		},
		{
			name:   "user",
			signed: strings.Replace(webSignedCheckUpdates, `"user_id":42`, `"user_id":43`, 1),
		},
		{
			name:   "tenant",
			signed: strings.Replace(webSignedCheckUpdates, `"tenant_id":100`, `"tenant_id":101`, 1),
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)

			cmd := f.deliver(tc.signed)
			if tc.mutate != nil {
				tc.mutate(&cmd)
			}
			f.process(cmd)

			f.requireRejected(t, "canonical payload mismatch")
		})
	}
}

// Intent: A command signed as a reboot but delivered as check_updates must not reboot the machine; the
// destructive path is selected by the type the command is bound to, never by an unsigned field.
func TestProcess_SignedRebootDeliveredAsCheckUpdates_DoesNotReboot(t *testing.T) {
	f := newSignedFixture(t)

	signed := strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"reboot"`, 1)
	f.process(f.deliver(signed))

	f.requireRejected(t, "canonical payload mismatch")
}

// Intent: A valid signature over bytes other than the delivered payload is an invalid signature, even when
// both byte strings are well-formed payloads for the same command.
func TestProcess_SignatureOverOtherBytes_InvalidSignature(t *testing.T) {
	cases := []struct {
		name      string
		signed    string
		delivered string
	}{
		{
			name:      "trailing whitespace",
			signed:    webSignedCheckUpdates,
			delivered: webSignedCheckUpdates + " ",
		},
		{
			name:      "reordered keys",
			signed:    webSignedCheckUpdates,
			delivered: `{"command_type":"check_updates","command_id":"11111111-2222-3333-4444-555555555555","expires_at":"2026-10-11T12:10:00.000Z","machine_id":200,"nonce":"0123456789abcdef0123456789abcdef","tenant_id":100,"timestamp":"2026-10-11T12:00:00.000Z","user_id":42}`,
		},
		{
			name:      "different field value",
			signed:    webSignedCheckUpdates,
			delivered: strings.Replace(webSignedCheckUpdates, `"nonce":"0123456789abcdef0123456789abcdef"`, `"nonce":"ffffffffffffffffffffffffffffffff"`, 1),
		},
		{
			name:      "delivered payload is not even a payload",
			signed:    webSignedCheckUpdates,
			delivered: `not json`,
		},
		{
			name:      "empty delivered payload",
			signed:    webSignedCheckUpdates,
			delivered: ``,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)

			cmd := f.deliver(tc.signed)
			cmd.CanonicalPayload = tc.delivered
			f.process(cmd)

			f.requireRejected(t, "invalid signature")
		})
	}
}

// Intent: A correctly signed payload that carries an unknown, duplicated or case-variant key is rejected as a
// payload error. The signature being valid does not make an ambiguous payload acceptable: encoding/json would
// otherwise let the last duplicate win and match "COMMAND_TYPE" to command_type.
func TestProcess_SignedAmbiguousPayload_CanonicalPayloadError(t *testing.T) {
	cases := []struct {
		name   string
		signed string
	}{
		{
			name:   "unknown key",
			signed: strings.Replace(webSignedCheckUpdates, `"nonce"`, `"extra":"x","nonce"`, 1),
		},
		{
			name:   "duplicate command_type, benign value last",
			signed: strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"reboot","command_type":"check_updates"`, 1),
		},
		{
			name:   "duplicate command_type, benign value first",
			signed: strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"check_updates","command_type":"reboot"`, 1),
		},
		{
			name:   "duplicate nonce",
			signed: strings.Replace(webSignedCheckUpdates, `"nonce":"0123456789abcdef0123456789abcdef"`, `"nonce":"0123456789abcdef0123456789abcdef","nonce":"0123456789abcdef0123456789abcdef"`, 1),
		},
		{
			name:   "case variant instead of the key",
			signed: strings.Replace(webSignedCheckUpdates, `"command_type"`, `"COMMAND_TYPE"`, 1),
		},
		{
			name:   "case variant alongside the key",
			signed: strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"check_updates","COMMAND_TYPE":"check_updates"`, 1),
		},
		{
			name:   "duplicate key inside params",
			signed: strings.Replace(webSignedCheckUpdates, `"tenant_id"`, `"params":{"a":"b","a":"b"},"tenant_id"`, 1),
		},
		{
			name:   "params null",
			signed: strings.Replace(webSignedCheckUpdates, `"tenant_id"`, `"params":null,"tenant_id"`, 1),
		},
		{
			name:   "missing key",
			signed: strings.Replace(webSignedCheckUpdates, `"nonce":"0123456789abcdef0123456789abcdef",`, ``, 1),
		},
		{
			name:   "trailing data",
			signed: webSignedCheckUpdates + `{}`,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)

			f.process(f.deliver(tc.signed))

			f.requireRejected(t, "canonical payload error")
		})
	}
}

// Intent: Timestamp validation runs on the signed timestamps against the processor's clock. A signed payload
// whose expiry has passed, or whose timestamp is older than the permitted age, is rejected without executing.
func TestProcess_SignedTimestamps_AreValidatedAgainstTheClock(t *testing.T) {
	cases := []struct {
		name        string
		now         time.Time
		wantMessage string
	}{
		{"expired", time.Date(2026, time.October, 11, 12, 10, 1, 0, time.UTC), "expired"},
		{"timestamp too old", time.Date(2026, time.October, 11, 12, 5, 1, 0, time.UTC), "too old"},
		{"timestamp too far in the future", time.Date(2026, time.October, 11, 11, 54, 59, 0, time.UTC), "future"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)
			f.processor.now = func() time.Time { return tc.now }

			f.process(f.deliver(webSignedCheckUpdates))

			f.requireRejected(t, tc.wantMessage)
		})
	}
}

// Intent: The same signed command is accepted at both edges of its validity window (five minutes either side
// of the signed timestamp), so the rejections above are caused by the clock and not by something else in the
// payload.
func TestProcess_SignedTimestamps_AcceptedWithinTheWindow(t *testing.T) {
	for _, now := range []time.Time{
		time.Date(2026, time.October, 11, 11, 55, 0, 0, time.UTC),
		time.Date(2026, time.October, 11, 12, 5, 0, 0, time.UTC),
	} {
		f := newSignedFixture(t)
		f.processor.now = func() time.Time { return now }

		f.process(f.deliver(webSignedCheckUpdates))

		if len(f.ack.calls) != 1 || f.ack.calls[0].ResultType != ResultTypeCompleted {
			t.Errorf("at %s expected a completed command, got %+v", now.Format(time.RFC3339), f.ack.calls)
		}
	}
}

// Intent: A signed payload cannot be replayed. The nonce comes from the signed payload and is recorded once.
func TestProcess_SignedPayload_NonceReplayRejected(t *testing.T) {
	f := newSignedFixture(t)
	f.store.isUsedFn = func(nonce string) (bool, error) { return nonce == signedTestNonce, nil }

	f.process(f.deliver(webSignedCheckUpdates))

	if len(f.ack.calls) != 1 || strings.Contains(f.ack.calls[0].Message, "nonce already used") == false {
		t.Fatalf("expected a nonce replay rejection, got %+v", f.ack.calls)
	}
	if len(*f.executed) != 0 {
		t.Errorf("a replayed command must not execute, executed %+v", *f.executed)
	}
}
