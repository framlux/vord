// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package crypto provides Ed25519 signature verification for remote commands.
package crypto

import (
	"bytes"
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
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

// BuildCanonicalPayload constructs a canonical JSON representation with sorted keys, always writing params.
// The agent verifies the bytes the server delivers rather than rebuilding them, so this serves tests that
// need to produce a signed payload.
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

// ValidateTimestamps checks, as of now, that the command has not expired, the timestamp is
// not too old, and the timestamp is not too far in the future (clock skew guard).
func ValidateTimestamps(now time.Time, timestampStr string, expiresAtStr string, maxAge time.Duration) error {
	timestamp, err := time.Parse(time.RFC3339, timestampStr)
	if err != nil {
		return fmt.Errorf("invalid timestamp: %w", err)
	}

	expiresAt, err := time.Parse(time.RFC3339, expiresAtStr)
	if err != nil {
		return fmt.Errorf("invalid expires_at: %w", err)
	}

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

// BoundCommand carries the fields of a delivered command that a signed payload must agree with.
type BoundCommand struct {
	CommandID   string
	CommandType string
	ExpiresAt   string
	MachineID   int64
	Nonce       string
	Params      map[string]string
	TenantID    int32
	Timestamp   string
	UserID      int32
}

// canonicalPayloadKeys lists the only keys a signed payload may carry. Every key except "params" must be
// present; an absent "params" means no parameters.
var canonicalPayloadKeys = []string{
	"command_id",
	"command_type",
	"expires_at",
	"machine_id",
	"nonce",
	"params",
	"tenant_id",
	"timestamp",
	"user_id",
}

// ParseCanonicalPayload decodes a signed canonical payload. An absent "params" decodes as an empty map.
// Unknown keys, duplicate keys and keys that differ only in case from a known key are all errors. Go's
// encoding/json matches keys case-insensitively and lets the last duplicate win, so the top level is walked
// token by token with json.Decoder.Token before it is decoded.
func ParseCanonicalPayload(data []byte) (CanonicalPayload, error) {
	var payload CanonicalPayload

	if utf8.Valid(data) == false {
		return payload, errors.New("canonical payload is not valid UTF-8")
	}

	dec := json.NewDecoder(bytes.NewReader(data))
	// Numbers stay as text so 64-bit identifiers are parsed as integers rather than rounded through float64.
	dec.UseNumber()

	if err := expectDelim(dec, '{', "canonical payload is not a JSON object"); err != nil {
		return payload, err
	}

	seen := make(map[string]bool, len(canonicalPayloadKeys))
	for dec.More() {
		key, err := readKey(dec)
		if err != nil {
			return payload, err
		}

		if slices.Contains(canonicalPayloadKeys, key) == false {
			if known, found := caseVariantOf(key); found {
				return payload, fmt.Errorf("canonical payload key %q differs only in case from %q", key, known)
			}

			return payload, fmt.Errorf("canonical payload has unknown key %q", key)
		}
		if seen[key] {
			return payload, fmt.Errorf("canonical payload has duplicate key %q", key)
		}
		seen[key] = true

		switch key {
		case "command_id":
			payload.CommandID, err = readString(dec, key)
		case "command_type":
			payload.CommandType, err = readString(dec, key)
		case "expires_at":
			payload.ExpiresAt, err = readString(dec, key)
		case "nonce":
			payload.Nonce, err = readString(dec, key)
		case "timestamp":
			payload.Timestamp, err = readString(dec, key)
		case "machine_id":
			payload.MachineID, err = readInt64(dec, key)
		case "tenant_id":
			payload.TenantID, err = readInt32(dec, key)
		case "user_id":
			payload.UserID, err = readInt32(dec, key)
		case "params":
			payload.Params, err = readParams(dec)
		}
		if err != nil {
			return payload, err
		}
	}

	if err := expectDelim(dec, '}', "canonical payload object is not terminated"); err != nil {
		return payload, err
	}
	if _, err := dec.Token(); errors.Is(err, io.EOF) == false {
		return payload, errors.New("canonical payload has trailing data after the object")
	}

	for _, key := range canonicalPayloadKeys {
		if (key != "params") && (seen[key] == false) {
			return payload, fmt.Errorf("canonical payload is missing key %q", key)
		}
	}
	if payload.Params == nil {
		payload.Params = map[string]string{}
	}

	return payload, nil
}

// caseVariantOf reports the known key that key matches case-insensitively, if any.
func caseVariantOf(key string) (string, bool) {
	for _, known := range canonicalPayloadKeys {
		if strings.EqualFold(known, key) {
			return known, true
		}
	}

	return "", false
}

// expectDelim consumes the next token and requires it to be the given delimiter.
func expectDelim(dec *json.Decoder, want json.Delim, message string) error {
	tok, err := dec.Token()
	if err != nil {
		return fmt.Errorf("%s: %w", message, err)
	}
	if delim, ok := tok.(json.Delim); (ok == false) || (delim != want) {
		return errors.New(message)
	}

	return nil
}

// readKey reads an object key.
func readKey(dec *json.Decoder) (string, error) {
	tok, err := dec.Token()
	if err != nil {
		return "", fmt.Errorf("reading canonical payload key: %w", err)
	}
	key, ok := tok.(string)
	if ok == false {
		return "", errors.New("canonical payload key is not a string")
	}

	return key, nil
}

// readString reads a value that must be a JSON string; null and every other type are rejected.
func readString(dec *json.Decoder, key string) (string, error) {
	tok, err := dec.Token()
	if err != nil {
		return "", fmt.Errorf("reading canonical payload %s: %w", key, err)
	}
	value, ok := tok.(string)
	if ok == false {
		return "", fmt.Errorf("canonical payload %s must be a string", key)
	}

	return value, nil
}

// readInteger reads a value that must be a JSON integer that fits in the given number of bits.
func readInteger(dec *json.Decoder, key string, bits int) (int64, error) {
	tok, err := dec.Token()
	if err != nil {
		return 0, fmt.Errorf("reading canonical payload %s: %w", key, err)
	}
	number, ok := tok.(json.Number)
	if ok == false {
		return 0, fmt.Errorf("canonical payload %s must be an integer", key)
	}
	value, err := strconv.ParseInt(string(number), 10, bits)
	if err != nil {
		return 0, fmt.Errorf("canonical payload %s must be an integer of %d bits: %w", key, bits, err)
	}

	return value, nil
}

func readInt64(dec *json.Decoder, key string) (int64, error) {
	return readInteger(dec, key, 64)
}

func readInt32(dec *json.Decoder, key string) (int32, error) {
	value, err := readInteger(dec, key, 32)

	return int32(value), err
}

// readParams reads the params object. Every value must be a string and no key may repeat.
func readParams(dec *json.Decoder) (map[string]string, error) {
	if err := expectDelim(dec, '{', "canonical payload params must be an object"); err != nil {
		return nil, err
	}

	params := make(map[string]string)
	for dec.More() {
		name, err := readKey(dec)
		if err != nil {
			return nil, err
		}
		if _, found := params[name]; found {
			return nil, fmt.Errorf("canonical payload params has duplicate key %q", name)
		}

		tok, err := dec.Token()
		if err != nil {
			return nil, fmt.Errorf("reading canonical payload params value: %w", err)
		}
		value, ok := tok.(string)
		if ok == false {
			return nil, fmt.Errorf("canonical payload params value for %q must be a string", name)
		}
		params[name] = value
	}

	if err := expectDelim(dec, '}', "canonical payload params object is not terminated"); err != nil {
		return nil, err
	}

	return params, nil
}

// BindsTo reports whether the signed payload describes exactly this command: the same command id, type,
// nonce, tenant, machine and user; the same instants for timestamp and expires_at (each parsed as RFC 3339
// with optional fractional seconds); and equal params, where nil equals empty.
func (p CanonicalPayload) BindsTo(cmd BoundCommand) bool {
	if (p.CommandID != cmd.CommandID) || (p.CommandType != cmd.CommandType) || (p.Nonce != cmd.Nonce) ||
		(p.TenantID != cmd.TenantID) || (p.MachineID != cmd.MachineID) || (p.UserID != cmd.UserID) {
		return false
	}

	if sameInstant(p.Timestamp, cmd.Timestamp) == false {
		return false
	}
	if sameInstant(p.ExpiresAt, cmd.ExpiresAt) == false {
		return false
	}

	return maps.Equal(p.Params, cmd.Params)
}

// sameInstant reports whether both strings are RFC 3339 timestamps for the same instant.
func sameInstant(a string, b string) bool {
	first, err := time.Parse(time.RFC3339, a)
	if err != nil {
		return false
	}
	second, err := time.Parse(time.RFC3339, b)
	if err != nil {
		return false
	}

	return first.Equal(second)
}
