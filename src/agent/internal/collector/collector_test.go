// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"testing"
	"time"

	"github.com/framlux/vord/internal/state"
)

func TestNewRegistry(t *testing.T) {
	r := NewRegistry()
	if r == nil {
		t.Fatal("NewRegistry() returned nil")
	}

	if len(r.entries) != 0 {
		t.Errorf("new registry has %d entries, want 0", len(r.entries))
	}
}

func TestRegistryRegister(t *testing.T) {
	r := NewRegistry()

	// ServicesCollector uses its DefaultInterval (60s).
	c := NewServicesCollector()
	r.Register(c)

	if len(r.entries) != 1 {
		t.Fatalf("registry has %d entries, want 1", len(r.entries))
	}
	if r.entries[0].collector.Name() != "service_status" {
		t.Errorf("entry name = %q, want 'service_status'", r.entries[0].collector.Name())
	}
	if r.entries[0].interval != 60*time.Second {
		t.Errorf("entry interval = %v, want 60s", r.entries[0].interval)
	}
}

func TestRegistryMultipleCollectors(t *testing.T) {
	r := NewRegistry()

	r.Register(NewSSHSessionsCollector(state.New()))
	r.Register(NewHwHealthCollector())
	r.Register(NewPackagesCollector())
	r.Register(NewServicesCollector())

	if len(r.entries) != 4 {
		t.Fatalf("registry has %d entries, want 4", len(r.entries))
	}

	// Verify all names are unique.
	names := make(map[string]bool)
	for _, e := range r.entries {
		name := e.collector.Name()
		if names[name] {
			t.Errorf("duplicate collector name: %q", name)
		}
		names[name] = true
	}
}

func TestCollectorNames(t *testing.T) {
	tests := []struct {
		collector Collector
		wantName  string
	}{
		{NewSSHSessionsCollector(state.New()), "ssh_sessions"},
		{NewHwHealthCollector(), "hardware_health"},
		{NewPackagesCollector(), "package_updates"},
		{NewServicesCollector(), "service_status"},
	}

	for _, tt := range tests {
		t.Run(tt.wantName, func(t *testing.T) {
			if got := tt.collector.Name(); got != tt.wantName {
				t.Errorf("Name() = %q, want %q", got, tt.wantName)
			}
		})
	}
}

// Intent: Each collector returns its exact expected default interval.
func TestCollectorDefaultIntervals(t *testing.T) {
	tests := []struct {
		collector    Collector
		wantExact    time.Duration
	}{
		{NewSSHSessionsCollector(state.New()), 30 * time.Second},
		{NewHwHealthCollector(), 5 * time.Minute},
		{NewPackagesCollector(), 6 * time.Hour},
		{NewServicesCollector(), 60 * time.Second},
	}

	for _, tt := range tests {
		t.Run(tt.collector.Name(), func(t *testing.T) {
			interval := tt.collector.DefaultInterval()
			if interval != tt.wantExact {
				t.Errorf("DefaultInterval() = %v, want %v", interval, tt.wantExact)
			}
		})
	}
}
