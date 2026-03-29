// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package crypto provides Ed25519 signature verification for remote commands.
package crypto

import (
	"crypto/ed25519"
	"encoding/json"
	"fmt"
	"time"
)

// CanonicalPayload represents the fields that are signed for a remote command.
type CanonicalPayload struct {
	CommandID   string            `json:"command_id"`
	CommandType string            `json:"command_type"`
	ExpiresAt   string            `json:"expires_at"`
	MachineID   int64             `json:"machine_id"`
	Nonce       string            `json:"nonce"`
	Params      map[string]string `json:"params"`
	TenantID    int32             `json:"tenant_id"`
	Timestamp   string            `json:"timestamp"`
	UserID      int32             `json:"user_id"`
}

// BuildCanonicalPayload constructs the canonical JSON representation with sorted keys.
func BuildCanonicalPayload(p CanonicalPayload) ([]byte, error) {
	// Use a sorted map to ensure deterministic key ordering.
	ordered := make(map[string]interface{})
	ordered["command_id"] = p.CommandID
	ordered["command_type"] = p.CommandType
	ordered["expires_at"] = p.ExpiresAt
	ordered["machine_id"] = p.MachineID
	ordered["nonce"] = p.Nonce

	if p.Params != nil {
		// json.Marshal sorts map[string]string keys automatically.
		ordered["params"] = p.Params
	} else {
		ordered["params"] = map[string]string{}
	}

	ordered["tenant_id"] = p.TenantID
	ordered["timestamp"] = p.Timestamp
	ordered["user_id"] = p.UserID

	// Go's json.Marshal produces sorted keys for map[string]interface{}.
	data, err := json.Marshal(ordered)
	if err != nil {
		return nil, fmt.Errorf("marshaling canonical payload: %w", err)
	}

	return data, nil
}

// VerifySignature verifies an Ed25519 signature over the canonical payload.
func VerifySignature(publicKey ed25519.PublicKey, canonicalPayload []byte, signature []byte) bool {
	if len(publicKey) != ed25519.PublicKeySize {
		return false
	}
	if len(signature) != ed25519.SignatureSize {
		return false
	}

	return ed25519.Verify(publicKey, canonicalPayload, signature)
}

// ValidateTimestamps checks that the command has not expired, the timestamp is
// not too old, and the timestamp is not too far in the future (clock skew guard).
func ValidateTimestamps(timestampStr string, expiresAtStr string, maxAge time.Duration) error {
	timestamp, err := time.Parse(time.RFC3339, timestampStr)
	if err != nil {
		return fmt.Errorf("invalid timestamp: %w", err)
	}

	expiresAt, err := time.Parse(time.RFC3339, expiresAtStr)
	if err != nil {
		return fmt.Errorf("invalid expires_at: %w", err)
	}

	now := time.Now().UTC()

	if now.After(expiresAt) {
		return fmt.Errorf("command expired at %s", expiresAtStr)
	}

	if now.Sub(timestamp) > maxAge {
		return fmt.Errorf("command timestamp too old: %s (max age %s)", timestampStr, maxAge)
	}

	// Reject timestamps that are too far in the future to guard against clock
	// skew or compromised servers issuing future-dated commands.
	if timestamp.After(now.Add(maxAge)) {
		return fmt.Errorf("command timestamp too far in the future: %s (max skew %s)", timestampStr, maxAge)
	}

	return nil
}
