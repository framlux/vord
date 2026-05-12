// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"bytes"
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
	handler              *Handler
	store                NonceStore
	ack                  CommandAcknowledger
	destructiveDelay     time.Duration
	maxTimestampAge      time.Duration
}

// NewProcessor creates a command processor.
func NewProcessor(handler *Handler, store NonceStore, ack CommandAcknowledger, destructiveDelay time.Duration) *Processor {
	return &Processor{
		handler:          handler,
		store:            store,
		ack:              ack,
		destructiveDelay: destructiveDelay,
		maxTimestampAge:  5 * time.Minute,
	}
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

	// 0b. Verify command ownership: tenant and machine must match agent identity.
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

	// 2. Rebuild canonical payload from individual fields and verify Ed25519 signature.
	rebuiltPayload, err := crypto.BuildCanonicalPayload(crypto.CanonicalPayload{
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
		slog.Error("failed to build canonical payload, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "canonical payload error", ResultTypeRejected)

		return
	}

	// Verify the server-supplied canonical payload matches the locally rebuilt one.
	if bytes.Equal(rebuiltPayload, []byte(cmd.CanonicalPayload)) == false {
		slog.Error("canonical payload mismatch, rejecting command", "command_id", cmd.ID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "canonical payload mismatch", ResultTypeRejected)

		return
	}

	if crypto.VerifySignature(key.PublicKey, rebuiltPayload, cmd.Signature) == false {
		slog.Error("invalid signature, rejecting command", "command_id", cmd.ID)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "invalid signature", ResultTypeRejected)

		return
	}

	// 3. Validate timestamps.
	if err := crypto.ValidateTimestamps(cmd.Timestamp, cmd.ExpiresAt, p.maxTimestampAge); err != nil {
		slog.Error("timestamp validation failed, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", err.Error(), ResultTypeRejected)

		return
	}

	// 4. Check nonce.
	used, err := p.store.IsNonceUsed(cmd.Nonce)
	if err != nil {
		slog.Error("nonce check failed", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce check error", ResultTypeRejected)

		return
	}
	if used {
		slog.Warn("nonce already used, rejecting replay", "command_id", cmd.ID, "nonce", cmd.Nonce)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce already used", ResultTypeRejected)

		return
	}

	// 5. Record nonce — reject the command if recording fails to prevent replay.
	if err := p.store.RecordNonce(cmd.Nonce); err != nil {
		slog.Error("failed to record nonce, rejecting command", "command_id", cmd.ID, "error", err)
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, false, -1, "", "", "nonce recording failed", ResultTypeRejected)

		return
	}

	// 6. Execute command, handling destructive commands with a delay.
	if cmdType.IsDestructive() {
		// ACK with Initiated before executing destructive command.
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, true, 0, "", "", "will execute in 10s", ResultTypeInitiated)

		// Wait to allow ACK to flush to server, but respect context cancellation.
		select {
		case <-time.After(p.destructiveDelay):
		case <-ctx.Done():
			return
		}

		result := p.handler.Execute(ctx, Command{
			Type:   cmdType,
			Params: cmd.Params,
		})

		// Always ACK destructive command results so the server knows the outcome.
		resultType := ResultTypeCompleted
		if result.Success == false {
			resultType = ResultTypeFailed
		}
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, result.Success, 0, "", "", result.Message, resultType)

		if result.Error != nil {
			slog.Error("destructive command failed", "command_id", cmd.ID, "type", cmd.Type, "error", result.Error)
		} else {
			slog.Info("destructive command executed", "command_id", cmd.ID, "type", cmd.Type, "message", result.Message)
		}
	} else {
		// Execute immediately and ACK with full result.
		result := p.handler.Execute(ctx, Command{
			Type:   cmdType,
			Params: cmd.Params,
		})

		resultType := ResultTypeCompleted
		if result.Success == false {
			resultType = ResultTypeFailed
		}
		p.ack.Acknowledge(ctx, cmd.ID, agentMachineID, result.Success, 0, "", "", result.Message, resultType)

		if result.Error != nil {
			slog.Error("command execution failed", "command_id", cmd.ID, "type", cmd.Type, "error", result.Error)
		} else {
			slog.Info("command executed", "command_id", cmd.ID, "type", cmd.Type, "message", result.Message)
		}
	}
}
