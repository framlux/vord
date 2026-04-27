// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"context"
	"time"

	"github.com/framlux/vord/internal/db"
)

// Collector is the interface that all telemetry collectors must implement.
type Collector interface {
	// Name returns a unique identifier for this collector (e.g. "system_info").
	Name() string

	// Collect gathers telemetry data and stores it in the database via the store.
	Collect(ctx context.Context, store *db.Store) error

	// DefaultInterval returns the default collection interval for this collector.
	DefaultInterval() time.Duration
}

// Registry holds all registered collectors with their configured intervals.
type Registry struct {
	entries []entry
}

type entry struct {
	collector   Collector
	interval    time.Duration
	getInterval func() time.Duration
}

// NewRegistry creates an empty collector registry.
func NewRegistry() *Registry {
	return &Registry{}
}

// Register adds a collector with its default interval to the registry.
func (r *Registry) Register(c Collector) {
	r.entries = append(r.entries, entry{
		collector: c,
		interval:  c.DefaultInterval(),
	})
}

// RegisterDynamic adds a collector with a dynamic interval function that is
// checked after each tick, allowing runtime interval changes without restart.
func (r *Registry) RegisterDynamic(c Collector, getInterval func() time.Duration) {
	r.entries = append(r.entries, entry{
		collector:   c,
		interval:    c.DefaultInterval(),
		getInterval: getInterval,
	})
}

