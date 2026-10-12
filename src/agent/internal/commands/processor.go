// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"log/slog"
	"time"

	"github.com/framlux/vord/internal/crypto"
)

// NonceStore abstracts the nonce-related operations needed by the command processor.
type NonceStore interface {
	IsNonceUsed(nonce string) (bool, error)
	RecordNonce(nonce string) error
	GetSigningKey(keyID int32) (*SigningKey, error)
}

// SigningKey is a local representation of a trusted signing key.
type SigningKey struct {
	KeyID     int32
	UserID    int32
	PublicKey []byte
}

// CommandAcknowledger sends acknowledgement results back to the server.
type CommandAcknowledger interface {
	Acknowledge(ctx context.Context, commandID string, machineID int64, success bool, exitCode int32, stdout string, stderr string, message string, resultType int32)
}

// ResultTypes are integer constants matching the protobuf ResultType enum values.
// Defined here so the processor package does not depend on generated proto code.
const (
	ResultTypeCompleted int32 = 0
	ResultTypeInitiated int32 = 1
	ResultTypeRejected  int32 = 2
	ResultTypeFailed    int32 = 3
)

// PendingCommand represents a command received from the server, with all fields
// needed for validation and execution.
type PendingCommand struct {
	ID               string
	Type             string
	Params           map[string]string
	CanonicalPayload string
	Signature        []byte
	SigningKeyID     int32
	Timestamp        string
	ExpiresAt        string
	Nonce            string
	UserID           int32
	TenantID         int32
	MachineID        int64
}

// Processor validates and executes remote commands.
type Processor struct {
	handler          *Handler
	store            NonceStore
	ack              CommandAcknowledger
	destructiveDelay time.Duration
	maxTimestampAge  time.Duration
	now              func() time.Time

	allowAll           bool
	localRecordPresent func() bool
}

// messageRemoteCommandsDisabled is what a command refused for the host's remote commands setting is acknowledged
// with.
const messageRemoteCommandsDisabled = "remote commands are disabled on this host"

// ProcessorOptions says which commands a Processor accepts.
type ProcessorOptions struct {
	// AllowAll is whether this host allows remote commands. When it is false the processor accepts only the
	// cleanup commands, and only while LocalRecordPresent says the host has something of its own to clean up.
	AllowAll bool

	// LocalRecordPresent answers whether this host holds a local reporter record. It is read for each cleanup
	// command, only when AllowAll is false. Nil means no record, so every command is refused.
	LocalRecordPresent func() bool
}

// NewProcessor creates a command processor.
func NewProcessor(handler *Handler, store NonceStore, ack CommandAcknowledger, destructiveDelay time.Duration, options ProcessorOptions) *Processor {
	return &Processor{
		handler:            handler,
		store:              store,
		ack:                ack,
		destructiveDelay:   destructiveDelay,
		maxTimestampAge:    5 * time.Minute,
		now:                time.Now,
		allowAll:           options.AllowAll,
		localRecordPresent: options.LocalRecordPresent,
	}
}

// acceptsWhileRemoteCommandsOff reports whether a command of this type may run on a host that has remote
// commands off: a cleanup command, on a host that holds a local reporter record.
func (p *Processor) acceptsWhileRemoteCommandsOff(commandType CommandType) bool {
	if commandType.IsCleanup() == false {
		return false
	}

	return (p.localRecordPresent != nil) && p.localRecordPresent()
}

// Process validates and executes a single command. It performs ownership checks,
// signature verification, nonce replay prevention, and dispatches execution.
func (p *Processor) Process(ctx context.Context, cmd PendingCommand, agentTenantID int32, agentMachineID int64) {
	cmdType := CommandType(cmd.Type)

	// 0a. Validate the command type before any further processing.
	if cmdType.IsValid() == false {
		slog.Error("unknown command type, rejecting", "command_id", cmd.ID, "type", cmd.Type)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "unknown command type", ResultTypeRejected)

		return
	}

	// 0b. With remote commands off, refuse everything but the cleanup commands before any other work: no
	// signature is checked, no nonce is spent, and a destructive command is not even acknowledged as initiated.
	if (p.allowAll == false) && (p.acceptsWhileRemoteCommandsOff(cmdType) == false) {
		slog.Warn("remote commands are disabled on this host, rejecting command", "command_id", cmd.ID, "type", cmd.Type)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", messageRemoteCommandsDisabled, ResultTypeRejected)

		return
	}

	// 0c. Verify command ownership: tenant and machine must match agent identity.
	if cmd.TenantID != agentTenantID {
		slog.Error("command tenant mismatch, rejecting", "command_id", cmd.ID, "cmd_tenant", cmd.TenantID, "agent_tenant", agentTenantID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "tenant mismatch", ResultTypeRejected)

		return
	}
	if cmd.MachineID != agentMachineID {
		slog.Error("command machine mismatch, rejecting", "command_id", cmd.ID, "cmd_machine", cmd.MachineID, "agent_machine", agentMachineID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "machine mismatch", ResultTypeRejected)

		return
	}

	// 1. Look up signing key.
	key, err := p.store.GetSigningKey(cmd.SigningKeyID)
	if err != nil {
		slog.Error("unknown signing key, rejecting command", "command_id", cmd.ID, "key_id", cmd.SigningKeyID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "unknown signing key", ResultTypeRejected)

		return
	}

	// 1a. Authorize the command's UserID against the user bound to the signing key.
	// A signing key is issued to a specific user; a command must be attributed to
	// that same user. Rejecting here prevents one user's key from authorizing
	// commands claimed under another user's identity.
	if cmd.UserID != key.UserID {
		slog.Error("command user does not match signing key owner, rejecting", "command_id", cmd.ID, "cmd_user", cmd.UserID, "key_user", key.UserID, "key_id", cmd.SigningKeyID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "user/key mismatch", ResultTypeRejected)

		return
	}

	// 2. Verify the Ed25519 signature over the exact bytes the server delivered, before reading anything in them.
	signedBytes := []byte(cmd.CanonicalPayload)
	if crypto.VerifySignature(key.PublicKey, signedBytes, cmd.Signature) == false {
		slog.Error("invalid signature, rejecting command", "command_id", cmd.ID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "invalid signature", ResultTypeRejected)

		return
	}

	// 2a. Decode the signed bytes strictly, so what is read is unambiguous.
	payload, err := crypto.ParseCanonicalPayload(signedBytes)
	if err != nil {
		slog.Error("canonical payload could not be parsed, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "canonical payload error", ResultTypeRejected)

		return
	}

	// 2b. Every signed field must describe the command that was delivered. The signed timestamps are compared as
	// instants because the web client and the server render them differently.
	if payload.BindsTo(crypto.BoundCommand{
		CommandID:   cmd.ID,
		CommandType: cmd.Type,
		ExpiresAt:   cmd.ExpiresAt,
		MachineID:   cmd.MachineID,
		Nonce:       cmd.Nonce,
		Params:      cmd.Params,
		TenantID:    cmd.TenantID,
		Timestamp:   cmd.Timestamp,
		UserID:      cmd.UserID,
	}) == false {
		slog.Error("canonical payload does not match the delivered command, rejecting command", "command_id", cmd.ID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "canonical payload mismatch", ResultTypeRejected)

		return
	}

	// 3. Validate the signed timestamps.
	if err := crypto.ValidateTimestamps(p.now().UTC(), payload.Timestamp, payload.ExpiresAt, p.maxTimestampAge); err != nil {
		slog.Error("timestamp validation failed, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", err.Error(), ResultTypeRejected)

		return
	}

	// 4. Check nonce.
	used, err := p.store.IsNonceUsed(payload.Nonce)
	if err != nil {
		slog.Error("nonce check failed", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce check error", ResultTypeRejected)

		return
	}
	if used {
		slog.Warn("nonce already used, rejecting replay", "command_id", cmd.ID, "nonce", payload.Nonce)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce already used", ResultTypeRejected)

		return
	}

	// 5. Record nonce — reject the command if recording fails to prevent replay.
	if err := p.store.RecordNonce(payload.Nonce); err != nil {
		slog.Error("failed to record nonce, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce recording failed", ResultTypeRejected)

		return
	}

	// 6. Execute the signed type with the signed params. Binding made them equal to the command's own, and they
	// are the ones read from the bytes the signature covers.
	signedType := CommandType(payload.CommandType)
	if signedType.IsDestructive() {
		// ACK with Initiated before executing destructive command.
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, true, 0, "", "", "will execute in 10s", ResultTypeInitiated)

		// Wait to allow ACK to flush to server, but respect context cancellation.
		select {
		case <-time.After(p.destructiveDelay):
		case <-ctx.Done():
			return
		}

		result := p.handler.Execute(ctx, Command{
			Type:   signedType,
			Params: payload.Params,
		})

		// Always ACK destructive command results so the server knows the outcome.
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, result.Success, 0, "", "", result.Message, resultTypeOf(result))

		if result.Error != nil {
			slog.Error("destructive command failed", "command_id", cmd.ID, "type", payload.CommandType, "error", result.Error)
		} else {
			slog.Info("destructive command executed", "command_id", cmd.ID, "type", payload.CommandType, "message", result.Message)
		}
	} else {
		// Execute immediately and ACK with full result.
		result := p.handler.Execute(ctx, Command{
			Type:   signedType,
			Params: payload.Params,
		})

		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, result.Success, 0, "", "", result.Message, resultTypeOf(result))

		if result.Error != nil {
			slog.Error("command execution failed", "command_id", cmd.ID, "type", payload.CommandType, "error", result.Error)
		} else {
			slog.Info("command executed", "command_id", cmd.ID, "type", payload.CommandType, "message", result.Message)
		}
	}
}

// resultTypeOf maps an execution result to the result type acknowledged to the server: completed, failed, or
// rejected for a command the executor refused before doing anything.
func resultTypeOf(result Result) int32 {
	switch {
	case result.Success:
		return ResultTypeCompleted
	case result.Rejected:
		return ResultTypeRejected
	}

	return ResultTypeFailed
}
