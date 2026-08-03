// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
)

// agentVersionPayload is the JSON shape enqueued for agent version telemetry. The field name
// matches the AgentVersionRecord proto field so the sender can unmarshal it with protojson.
type agentVersionPayload struct {
	Version string `json:"version"`
}

// AgentVersionCollector reports the running agent's build version so operators can see which
// agents are out of date. The value is stamped into the binary at link time, so it is constant
// for the lifetime of the process and only changes when the agent is upgraded and restarted.
type AgentVersionCollector struct {
	version string
}

// NewAgentVersionCollector creates a collector that reports the supplied agent version.
func NewAgentVersionCollector(version string) *AgentVersionCollector {
	return &AgentVersionCollector{version: strings.TrimSpace(version)}
}

// Name returns the collector's unique identifier.
func (c *AgentVersionCollector) Name() string { return "agent_version" }

// DefaultInterval returns the collection interval, matching the other static, slow-changing facts.
func (c *AgentVersionCollector) DefaultInterval() time.Duration { return 1 * time.Hour }

// Collect enqueues the agent version. A binary built without a version stamp reports nothing at
// all rather than an empty string, so the server keeps whatever version it last recorded instead
// of overwriting it with a blank value.
func (c *AgentVersionCollector) Collect(_ context.Context, store *db.Store) error {
	if c.version == "" {
		slog.Debug("agent version is not stamped, skipping agent version telemetry")

		return store.SaveCollectorState(c.Name(), nil)
	}

	data, err := json.Marshal(agentVersionPayload{Version: c.version})
	if err != nil {
		return fmt.Errorf("marshaling agent version: %w", err)
	}

	if err := store.EnqueueTelemetry(id.NewV7(), db.TelemetryAgentVersion, string(data)); err != nil {
		return fmt.Errorf("enqueuing agent version telemetry: %w", err)
	}

	return store.SaveCollectorState(c.Name(), nil)
}
