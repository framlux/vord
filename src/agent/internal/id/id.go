// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package id provides UUID generation helpers for the vord agent.
package id

import (
	"log/slog"

	"github.com/google/uuid"
)

// NewV7 generates a new UUIDv7 string. If UUIDv7 generation fails (e.g. due to
// OS entropy issues), it falls back to UUIDv4 and logs a debug message.
func NewV7() string {
	id, err := uuid.NewV7()
	if err != nil {
		slog.Debug("UUIDv7 generation failed, falling back to UUIDv4", "error", err)
		return uuid.New().String()
	}
	return id.String()
}