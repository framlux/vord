// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"strings"
	"testing"
)

// Intent: Already-cancelled context returns an error immediately.
func TestRunCmd_ContextCancelled(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	_, err := runCmd(ctx, "echo", "hello")
	if err == nil {
		t.Error("expected error for cancelled context, got nil")
	}
}

// Intent: Valid command returns expected output.
func TestRunCmd_ValidCommand(t *testing.T) {
	out, err := runCmd(context.Background(), "echo", "hello")
	if err != nil {
		t.Fatalf("runCmd: %v", err)
	}

	if strings.TrimSpace(string(out)) != "hello" {
		t.Errorf("output = %q, want %q", strings.TrimSpace(string(out)), "hello")
	}
}

// Intent: Non-existent command returns an exec error.
func TestRunCmd_NonExistentCommand(t *testing.T) {
	_, err := runCmd(context.Background(), "nonexistent-command-xyz-12345")
	if err == nil {
		t.Error("expected error for non-existent command, got nil")
	}
}
