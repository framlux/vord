// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os/exec"
	"strings"
	"time"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/state"
)

// ServicesCollector collects systemd service status.
type ServicesCollector struct {
	hasSystemctl bool
	rs           *state.RuntimeState
}

// NewServicesCollector creates a new ServicesCollector.
func NewServicesCollector(rs *state.RuntimeState) *ServicesCollector {
	_, err := exec.LookPath("systemctl")

	return &ServicesCollector{hasSystemctl: err == nil, rs: rs}
}

func (c *ServicesCollector) Name() string                   { return "service_status" }
func (c *ServicesCollector) DefaultInterval() time.Duration { return c.rs.ServiceStatusInterval() }

type serviceEntry struct {
	Unit        string `json:"unit"`
	LoadState   string `json:"load_state"`
	ActiveState string `json:"active_state"`
	SubState    string `json:"sub_state"`
	Description string `json:"description"`
}

type servicesPayload struct {
	Services []serviceEntry `json:"services"`
}

func (c *ServicesCollector) Collect(ctx context.Context, store *db.Store) error {
	if c.hasSystemctl == false {
		slog.Debug("systemctl not available")
		return store.SaveCollectorState(c.Name(), nil)
	}

	out, err := runCmd(ctx, "systemctl", "list-units", "--type=service", "--all", "--no-pager", "--plain")
	if err != nil {
		return fmt.Errorf("running systemctl: %w", err)
	}

	services := parseSystemctlListUnits(string(out))

	payload := servicesPayload{Services: services}
	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshaling services: %w", err)
	}

	if err := store.EnqueueTelemetry(id.NewV7(), db.TelemetryServiceStatus, string(data)); err != nil {
		return fmt.Errorf("enqueuing service status telemetry: %w", err)
	}

	return store.SaveCollectorState(c.Name(), nil)
}

// parseSystemctlListUnits parses the output of "systemctl list-units --type=service" into service entries.
func parseSystemctlListUnits(output string) []serviceEntry {
	var services []serviceEntry
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()
		if line == "" {
			continue
		}

		// Skip header and footer lines.
		if strings.HasPrefix(line, "UNIT") || strings.HasPrefix(line, "LOAD") {
			continue
		}
		if strings.Contains(line, " loaded units listed") || strings.Contains(line, "To show all") {
			continue
		}

		fields := strings.Fields(line)
		if len(fields) < 4 {
			continue
		}

		// Format: UNIT LOAD ACTIVE SUB DESCRIPTION...
		unit := fields[0]
		if strings.HasSuffix(unit, ".service") == false {
			continue
		}

		description := ""
		if len(fields) > 4 {
			description = strings.Join(fields[4:], " ")
		}

		services = append(services, serviceEntry{
			Unit:        unit,
			LoadState:   fields[1],
			ActiveState: fields[2],
			SubState:    fields[3],
			Description: description,
		})
	}

	return services
}