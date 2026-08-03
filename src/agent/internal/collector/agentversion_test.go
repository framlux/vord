// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/framlux/vord/internal/db"
)

// Intent: A stamped agent version is enqueued as agent version telemetry with the version value.
func TestAgentVersionCollector_EnqueuesStampedVersion(t *testing.T) {
	store := newTestStore(t)
	c := NewAgentVersionCollector("1.16.0")

	if err := c.Collect(context.Background(), store); err != nil {
		t.Fatalf("Collect error: %v", err)
	}

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}
	if items[0].ItemType != db.TelemetryAgentVersion {
		t.Errorf("expected type %d, got %d", db.TelemetryAgentVersion, items[0].ItemType)
	}

	var payload agentVersionPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.Version != "1.16.0" {
		t.Errorf("expected version 1.16.0, got %q", payload.Version)
	}
}

// Intent: A binary with no version stamp enqueues nothing, so the server keeps the version it
// already recorded instead of having it overwritten with a blank value.
func TestAgentVersionCollector_UnstampedVersionEnqueuesNothing(t *testing.T) {
	for _, version := range []string{"", "   "} {
		store := newTestStore(t)
		c := NewAgentVersionCollector(version)

		if err := c.Collect(context.Background(), store); err != nil {
			t.Fatalf("Collect error for version %q: %v", version, err)
		}

		items, err := store.DequeueTelemetry(10)
		if err != nil {
			t.Fatalf("DequeueTelemetry error: %v", err)
		}
		if len(items) != 0 {
			t.Errorf("expected 0 telemetry items for version %q, got %d", version, len(items))
		}
	}
}

// Intent: Surrounding whitespace from the build stamp is trimmed before it is reported.
func TestAgentVersionCollector_TrimsVersion(t *testing.T) {
	store := newTestStore(t)
	c := NewAgentVersionCollector("  1.16.0-rc1\n")

	if err := c.Collect(context.Background(), store); err != nil {
		t.Fatalf("Collect error: %v", err)
	}

	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("DequeueTelemetry error: %v", err)
	}
	if len(items) != 1 {
		t.Fatalf("expected 1 telemetry item, got %d", len(items))
	}

	var payload agentVersionPayload
	if err := json.Unmarshal([]byte(items[0].Payload), &payload); err != nil {
		t.Fatalf("failed to unmarshal payload: %v", err)
	}
	if payload.Version != "1.16.0-rc1" {
		t.Errorf("expected version 1.16.0-rc1, got %q", payload.Version)
	}
}

// Intent: The collector identifies itself and runs on the slow, static-data cadence.
func TestAgentVersionCollector_NameAndInterval(t *testing.T) {
	c := NewAgentVersionCollector("1.16.0")

	if c.Name() != "agent_version" {
		t.Errorf("expected name agent_version, got %q", c.Name())
	}
	if c.DefaultInterval() != 1*time.Hour {
		t.Errorf("expected 1h interval, got %v", c.DefaultInterval())
	}
}
