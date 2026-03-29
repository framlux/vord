// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package state provides thread-safe runtime state for the vord agent.
package state

import (
	"sync"
	"time"
)

// RuntimeState holds thread-safe in-memory agent state.
type RuntimeState struct {
	mu sync.RWMutex

	machineID             int64
	tenantID              int32
	hostname              string
	isRegistered          bool
	serialNumber          string
	apiKey                string
	configRefreshInterval time.Duration
	pingInterval          time.Duration
	commandPollInterval   time.Duration
}

// New creates a new RuntimeState with default values.
func New() *RuntimeState {
	return &RuntimeState{
		configRefreshInterval: 5 * time.Minute,
		pingInterval:          60 * time.Second,
		commandPollInterval:   30 * time.Second,
	}
}

// MachineID returns the server-assigned machine ID.
func (s *RuntimeState) MachineID() int64 {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.machineID
}

// SetMachineID sets the server-assigned machine ID.
func (s *RuntimeState) SetMachineID(id int64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.machineID = id
}

// Hostname returns the machine hostname.
func (s *RuntimeState) Hostname() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.hostname
}

// SetHostname sets the machine hostname.
func (s *RuntimeState) SetHostname(h string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.hostname = h
}

// IsRegistered returns whether the agent has been registered.
func (s *RuntimeState) IsRegistered() bool {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.isRegistered
}

// SetRegistered sets the registration status.
func (s *RuntimeState) SetRegistered(r bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.isRegistered = r
}

// SerialNumber returns the hardware serial number.
func (s *RuntimeState) SerialNumber() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.serialNumber
}

// SetSerialNumber sets the hardware serial number.
func (s *RuntimeState) SetSerialNumber(sn string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.serialNumber = sn
}

// ApiKey returns the API key used for authenticating gRPC calls.
func (s *RuntimeState) ApiKey() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.apiKey
}

// SetApiKey sets the API key used for authenticating gRPC calls.
func (s *RuntimeState) SetApiKey(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.apiKey = key
}

// ConfigRefreshInterval returns the interval between configuration refresh polls.
func (s *RuntimeState) ConfigRefreshInterval() time.Duration {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.configRefreshInterval
}

// SetConfigRefreshInterval sets the interval between configuration refresh polls.
func (s *RuntimeState) SetConfigRefreshInterval(d time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.configRefreshInterval = d
}

// PingInterval returns the interval between heartbeat pings.
func (s *RuntimeState) PingInterval() time.Duration {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.pingInterval
}

// SetPingInterval sets the interval between heartbeat pings.
func (s *RuntimeState) SetPingInterval(d time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.pingInterval = d
}

// TenantID returns the tenant ID this machine belongs to.
func (s *RuntimeState) TenantID() int32 {
	s.mu.RLock()
	defer s.mu.RUnlock()

	return s.tenantID
}

// SetTenantID sets the tenant ID this machine belongs to.
func (s *RuntimeState) SetTenantID(id int32) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.tenantID = id
}

// CommandPollInterval returns the interval between command poll checks.
func (s *RuntimeState) CommandPollInterval() time.Duration {
	s.mu.RLock()
	defer s.mu.RUnlock()

	return s.commandPollInterval
}

// SetCommandPollInterval sets the interval between command poll checks.
func (s *RuntimeState) SetCommandPollInterval(d time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.commandPollInterval = d
}
