// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package db

// TelemetryType identifies the kind of telemetry data stored in the queue.
type TelemetryType int

const (
	TelemetrySystemInfo     TelemetryType = 1
	TelemetryOsVersion      TelemetryType = 2
	TelemetryCpuInfo        TelemetryType = 3
	TelemetryMemoryInfo     TelemetryType = 4
	TelemetryDiskInfo       TelemetryType = 5
	TelemetryCpuUsage       TelemetryType = 6
	TelemetryMemoryUsage    TelemetryType = 7
	TelemetryDiskUsage      TelemetryType = 8
	TelemetrySSHSession     TelemetryType = 9
	TelemetryHardwareHealth TelemetryType = 10
	TelemetryPackageUpdates TelemetryType = 11
	TelemetryServiceStatus  TelemetryType = 12
)

// QueueStatus represents the processing state of a telemetry queue item.
type QueueStatus int

const (
	QueueStatusPending    QueueStatus = 0
	QueueStatusProcessing QueueStatus = 1
	QueueStatusCompleted  QueueStatus = 2
	QueueStatusFailed     QueueStatus = 3
)

// AgentConfig represents a row in the agent_config table.
type AgentConfig struct {
	Key       string
	Value     string
	UpdatedAt string
}

// TelemetryQueueItem represents a row in the telemetry_queue table.
type TelemetryQueueItem struct {
	ID        string
	ItemType  TelemetryType
	Payload   string
	Status    QueueStatus
	CreatedAt string
	UpdatedAt string
}

// ServerConfig represents a row in the server_config table.
type ServerConfig struct {
	ID         int64
	ConfigJSON string
	AppliedAt  *string
	CreatedAt  string
}

// SSHSession represents a row in the ssh_sessions table.
type SSHSession struct {
	ID         string
	SessionID  *string
	User       string
	SourceIP   *string
	SourcePort *int
	Action     string
	AuthMethod *string
	Timestamp  string
	Queued     int
}

// CollectorState represents a row in the collector_state table.
type CollectorState struct {
	CollectorName string
	LastRunAt     *string
	StateJSON     *string
}