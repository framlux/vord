// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// Package jitter applies randomized timing offsets so a fleet of agents does not fire
// on identical schedules and overwhelm the server (thundering herd). The randomness source
// is injectable so tests are deterministic.
package jitter

import (
	mathrand "math/rand/v2"
	"time"
)

// Source supplies a uniform random float in [0.0, 1.0). math/rand/v2's Float64 satisfies it.
type Source interface {
	Float64() float64
}

// globalSource draws from the process-global, concurrency-safe math/rand/v2 generator.
type globalSource struct{}

func (globalSource) Float64() float64 { return mathrand.Float64() }

// NewDefault constructs a Jitter backed by the process-global math/rand/v2 source. The global
// generator is auto-seeded and safe for concurrent use, so a single Jitter can be shared by every
// agent loop without coordinating seeds.
func NewDefault() *Jitter {
	return New(globalSource{})
}

// Jitter applies bounded randomized offsets to durations.
type Jitter struct {
	src Source
}

// New constructs a Jitter over the given random source.
func New(src Source) *Jitter {
	return &Jitter{src: src}
}

// Apply returns base scaled by a random factor in [1-fraction, 1+fraction].
// A fraction of 0 returns base unchanged. Use a fraction of 0.1-0.2 for ±10-20% jitter.
func (j *Jitter) Apply(base time.Duration, fraction float64) time.Duration {
	if fraction <= 0 {
		return base
	}

	// Map [0,1) onto [-fraction, +fraction].
	delta := (j.src.Float64()*2 - 1) * fraction

	return time.Duration(float64(base) * (1 + delta))
}

// StartupPhase returns a random offset in [0, base) used to stagger goroutine start times
// so all agents do not begin their first cycle at the same instant.
func (j *Jitter) StartupPhase(base time.Duration) time.Duration {
	if base <= 0 {
		return 0
	}

	return time.Duration(float64(base) * j.src.Float64())
}
