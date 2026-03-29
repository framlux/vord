// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package state

import (
	"sync"
	"testing"
	"time"
)

// Intent: Default intervals must match documented values — configRefresh=5min, ping=60s.
func TestNewRuntimeState_Defaults(t *testing.T) {
	s := New()

	if s.ConfigRefreshInterval() != 5*time.Minute {
		t.Errorf("expected ConfigRefreshInterval=5m, got %v", s.ConfigRefreshInterval())
	}
	if s.PingInterval() != 60*time.Second {
		t.Errorf("expected PingInterval=60s, got %v", s.PingInterval())
	}
	if s.MachineID() != 0 {
		t.Errorf("expected MachineID=0, got %d", s.MachineID())
	}
	if s.IsRegistered() {
		t.Error("expected IsRegistered=false on new state")
	}
	if s.ApiKey() != "" {
		t.Errorf("expected empty ApiKey, got %q", s.ApiKey())
	}
	if s.Hostname() != "" {
		t.Errorf("expected empty Hostname, got %q", s.Hostname())
	}
	if s.SerialNumber() != "" {
		t.Errorf("expected empty SerialNumber, got %q", s.SerialNumber())
	}
}

// Intent: Set and retrieve machine ID round-trips correctly.
func TestSetGetMachineID(t *testing.T) {
	s := New()
	s.SetMachineID(42)

	if s.MachineID() != 42 {
		t.Errorf("expected MachineID=42, got %d", s.MachineID())
	}
}

// Intent: Set and retrieve API key round-trips correctly.
func TestSetGetAPIKey(t *testing.T) {
	s := New()
	s.SetApiKey("test-key-abc")

	if s.ApiKey() != "test-key-abc" {
		t.Errorf("expected ApiKey=%q, got %q", "test-key-abc", s.ApiKey())
	}
}

// Intent: Set and retrieve registered flag round-trips correctly.
func TestSetGetRegistered(t *testing.T) {
	s := New()
	s.SetRegistered(true)

	if s.IsRegistered() == false {
		t.Error("expected IsRegistered=true after SetRegistered(true)")
	}

	s.SetRegistered(false)
	if s.IsRegistered() {
		t.Error("expected IsRegistered=false after SetRegistered(false)")
	}
}

// Intent: Set and retrieve config refresh interval round-trips correctly.
func TestSetGetConfigRefreshInterval(t *testing.T) {
	s := New()
	s.SetConfigRefreshInterval(10 * time.Minute)

	if s.ConfigRefreshInterval() != 10*time.Minute {
		t.Errorf("expected ConfigRefreshInterval=10m, got %v", s.ConfigRefreshInterval())
	}
}

// Intent: Set and retrieve ping interval round-trips correctly.
func TestSetGetPingInterval(t *testing.T) {
	s := New()
	s.SetPingInterval(30 * time.Second)

	if s.PingInterval() != 30*time.Second {
		t.Errorf("expected PingInterval=30s, got %v", s.PingInterval())
	}
}

// Intent: Set and retrieve hostname round-trips correctly.
func TestSetGetHostname(t *testing.T) {
	s := New()
	s.SetHostname("test-host")

	if s.Hostname() != "test-host" {
		t.Errorf("expected Hostname=%q, got %q", "test-host", s.Hostname())
	}
}

// Intent: Set and retrieve serial number round-trips correctly.
func TestSetGetSerialNumber(t *testing.T) {
	s := New()
	s.SetSerialNumber("SN-12345")

	if s.SerialNumber() != "SN-12345" {
		t.Errorf("expected SerialNumber=%q, got %q", "SN-12345", s.SerialNumber())
	}
}

// Intent: Concurrent access from multiple goroutines must not cause data races.
func TestConcurrentAccess(t *testing.T) {
	s := New()
	var wg sync.WaitGroup

	for i := 0; i < 100; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			s.SetMachineID(int64(n))
			s.SetApiKey("key")
			s.SetRegistered(true)
			s.SetHostname("host")
			s.SetSerialNumber("sn")
			s.SetConfigRefreshInterval(time.Duration(n) * time.Second)
			s.SetPingInterval(time.Duration(n) * time.Second)

			_ = s.MachineID()
			_ = s.ApiKey()
			_ = s.IsRegistered()
			_ = s.Hostname()
			_ = s.SerialNumber()
			_ = s.ConfigRefreshInterval()
			_ = s.PingInterval()
		}(i)
	}

	wg.Wait()
}

// Intent: Zero-value RuntimeState returns expected defaults without panicking.
func TestZeroValueSafety(t *testing.T) {
	var s RuntimeState

	if s.MachineID() != 0 {
		t.Errorf("expected MachineID=0 on zero-value, got %d", s.MachineID())
	}
	if s.ApiKey() != "" {
		t.Errorf("expected empty ApiKey on zero-value, got %q", s.ApiKey())
	}
	if s.IsRegistered() {
		t.Error("expected IsRegistered=false on zero-value")
	}
	if s.Hostname() != "" {
		t.Errorf("expected empty Hostname on zero-value, got %q", s.Hostname())
	}
	if s.SerialNumber() != "" {
		t.Errorf("expected empty SerialNumber on zero-value, got %q", s.SerialNumber())
	}
	if s.ConfigRefreshInterval() != 0 {
		t.Errorf("expected ConfigRefreshInterval=0 on zero-value, got %v", s.ConfigRefreshInterval())
	}
	if s.PingInterval() != 0 {
		t.Errorf("expected PingInterval=0 on zero-value, got %v", s.PingInterval())
	}
}
