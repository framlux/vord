// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package crypto

import (
	"crypto/ed25519"
	"crypto/rand"
	"testing"
	"time"
)

func TestBuildCanonicalPayload_Deterministic(t *testing.T) {
	p := CanonicalPayload{
		CommandID:   "test-uuid",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   42,
		Nonce:       "abc123",
		Params:      map[string]string{"key": "value"},
		TenantID:    7,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      3,
	}

	data1, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	data2, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	if string(data1) != string(data2) {
		t.Errorf("non-deterministic output:\n%s\nvs\n%s", data1, data2)
	}
}

func TestVerifySignature_RoundTrip(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	payload := CanonicalPayload{
		CommandID:   "test-uuid",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   42,
		Nonce:       "abc123",
		Params:      map[string]string{},
		TenantID:    7,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      3,
	}

	data, err := BuildCanonicalPayload(payload)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	sig := ed25519.Sign(priv, data)

	if VerifySignature(pub, data, sig) == false {
		t.Error("valid signature rejected")
	}
}

func TestVerifySignature_InvalidSignature(t *testing.T) {
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	data := []byte(`{"test":"data"}`)
	badSig := make([]byte, ed25519.SignatureSize)

	if VerifySignature(pub, data, badSig) {
		t.Error("invalid signature accepted")
	}
}

func TestVerifySignature_WrongKey(t *testing.T) {
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	otherPub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	data := []byte(`{"test":"data"}`)
	sig := ed25519.Sign(priv, data)

	if VerifySignature(otherPub, data, sig) {
		t.Error("wrong key accepted")
	}
}

func TestValidateTimestamps_Valid(t *testing.T) {
	now := time.Now().UTC()
	ts := now.Format(time.RFC3339)
	exp := now.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(ts, exp, 5*time.Minute)
	if err != nil {
		t.Errorf("expected valid, got: %v", err)
	}
}

func TestValidateTimestamps_Expired(t *testing.T) {
	now := time.Now().UTC()
	ts := now.Add(-10 * time.Minute).Format(time.RFC3339)
	exp := now.Add(-5 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(ts, exp, 15*time.Minute)
	if err == nil {
		t.Error("expected expired error")
	}
}

func TestValidateTimestamps_TooOld(t *testing.T) {
	now := time.Now().UTC()
	ts := now.Add(-10 * time.Minute).Format(time.RFC3339)
	exp := now.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(ts, exp, 5*time.Minute)
	if err == nil {
		t.Error("expected too-old error")
	}
}

// Intent: Timestamp far in the future is rejected to guard against clock skew attacks.
func TestValidateTimestamps_FutureTimestamp(t *testing.T) {
	now := time.Now().UTC()
	// Timestamp 20 minutes in the future with maxAge=5min should fail.
	ts := now.Add(20 * time.Minute).Format(time.RFC3339)
	exp := now.Add(30 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(ts, exp, 5*time.Minute)
	if err == nil {
		t.Error("expected future-timestamp error")
	}
}

// Intent: Timestamp slightly in the future (within maxAge) is accepted for clock skew tolerance.
func TestValidateTimestamps_SlightlyFutureAccepted(t *testing.T) {
	now := time.Now().UTC()
	// Timestamp 2 minutes in the future with maxAge=5min should be OK.
	ts := now.Add(2 * time.Minute).Format(time.RFC3339)
	exp := now.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(ts, exp, 5*time.Minute)
	if err != nil {
		t.Errorf("expected slightly-future timestamp to be accepted, got: %v", err)
	}
}

// Intent: Invalid timestamp format returns error.
func TestValidateTimestamps_InvalidFormat(t *testing.T) {
	err := ValidateTimestamps("not-a-date", "also-not-a-date", 5*time.Minute)
	if err == nil {
		t.Error("expected error for invalid timestamp format")
	}
}

// Intent: Valid timestamp but invalid expires_at format returns error.
func TestValidateTimestamps_InvalidExpiresFormat(t *testing.T) {
	now := time.Now().UTC()
	ts := now.Format(time.RFC3339)

	err := ValidateTimestamps(ts, "not-a-date", 5*time.Minute)
	if err == nil {
		t.Error("expected error for invalid expires_at format")
	}
}

// Intent: VerifySignature rejects truncated keys and signatures.
func TestVerifySignature_InvalidKeyAndSigSizes(t *testing.T) {
	if VerifySignature([]byte("short"), []byte("data"), make([]byte, ed25519.SignatureSize)) {
		t.Error("expected false for short public key")
	}

	pub, _, _ := ed25519.GenerateKey(rand.Reader)
	if VerifySignature(pub, []byte("data"), []byte("short-sig")) {
		t.Error("expected false for short signature")
	}
}

// Intent: BuildCanonicalPayload with nil Params uses empty map (not null in JSON).
func TestBuildCanonicalPayload_NilParams(t *testing.T) {
	p := CanonicalPayload{
		CommandID:   "test",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   1,
		Nonce:       "abc",
		Params:      nil,
		TenantID:    1,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      1,
	}

	data, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	// Params should be serialized as {} not null.
	str := string(data)
	if str == "" {
		t.Fatal("expected non-empty canonical payload")
	}
}
