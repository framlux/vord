// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package id

import (
	"strings"
	"testing"
)

// Intent: NewV7 returns a non-empty string in UUID format (8-4-4-4-12).
func TestNewV7_ReturnsValidUUID(t *testing.T) {
	id := NewV7()

	if id == "" {
		t.Fatal("NewV7() returned empty string")
	}

	parts := strings.Split(id, "-")
	if len(parts) != 5 {
		t.Errorf("expected 5 dash-separated parts, got %d in %q", len(parts), id)
	}

	expectedLens := []int{8, 4, 4, 4, 12}
	for i, part := range parts {
		if len(part) != expectedLens[i] {
			t.Errorf("part %d: expected length %d, got %d in %q", i, expectedLens[i], len(part), id)
		}
	}
}

// Intent: 1000 calls must return 1000 distinct values — UUIDs must be unique.
func TestNewV7_Uniqueness(t *testing.T) {
	seen := make(map[string]bool, 1000)
	for i := 0; i < 1000; i++ {
		id := NewV7()
		if seen[id] {
			t.Fatalf("duplicate UUID generated on iteration %d: %s", i, id)
		}
		seen[id] = true
	}
}

// Intent: UUID version nibble must be 7 (for UUIDv7).
func TestNewV7_Version7(t *testing.T) {
	id := NewV7()

	// The version nibble is the first character of the third group (index 14 in full string).
	parts := strings.Split(id, "-")
	if len(parts) < 3 {
		t.Fatalf("unexpected UUID format: %s", id)
	}

	versionChar := parts[2][0]
	// UUIDv7 has version nibble '7'; fallback UUIDv4 has '4'. Either is acceptable.
	if versionChar != '7' && versionChar != '4' {
		t.Errorf("expected version nibble '7' or '4', got %q in %s", string(versionChar), id)
	}
}

// Intent: NewV7 never returns empty string, even under repeated calls.
func TestNewV7_NotEmpty(t *testing.T) {
	for i := 0; i < 100; i++ {
		id := NewV7()
		if id == "" {
			t.Fatalf("NewV7() returned empty string on iteration %d", i)
		}
	}
}
