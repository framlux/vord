// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

package jitter

import (
	"testing"
	"time"
)

// fixedSource returns a constant fraction so tests are deterministic.
type fixedSource struct{ v float64 }

func (f fixedSource) Float64() float64 { return f.v }

func TestApply_ZeroFraction_ReturnsBase(t *testing.T) {
	j := New(fixedSource{v: 0.5})
	got := j.Apply(100*time.Second, 0)
	if got != 100*time.Second {
		t.Fatalf("expected base unchanged, got %v", got)
	}
}

func TestApply_WithinBounds(t *testing.T) {
	// source 0.0 -> -fraction, source 1.0 -> +fraction
	low := New(fixedSource{v: 0.0}).Apply(100*time.Second, 0.2)
	high := New(fixedSource{v: 1.0}).Apply(100*time.Second, 0.2)
	if low != 80*time.Second {
		t.Fatalf("expected 80s at low bound, got %v", low)
	}
	if high != 120*time.Second {
		t.Fatalf("expected 120s at high bound, got %v", high)
	}
}

func TestApply_MidpointIsBase(t *testing.T) {
	got := New(fixedSource{v: 0.5}).Apply(100*time.Second, 0.2)
	if got != 100*time.Second {
		t.Fatalf("expected base at midpoint, got %v", got)
	}
}

func TestStartupPhase_NeverExceedsBase(t *testing.T) {
	got := New(fixedSource{v: 1.0}).StartupPhase(30 * time.Second)
	if got < 0 || got > 30*time.Second {
		t.Fatalf("startup phase out of range: %v", got)
	}
}
