// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package registration handles the agent registration lifecycle with the vord server.
package registration

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"

	"github.com/framlux/vord/internal/db"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
)

// Manager handles the agent registration lifecycle.
type Manager struct {
	registration      pb.RegistrationClient
	configuration     pb.ConfigurationClient
	store             *db.Store
	state             *state.RuntimeState
	registrationToken string
}

// NewManager creates a new registration manager. It accepts the specific gRPC
// service interfaces rather than the concrete Client type so that tests can
// inject mocks without constructing a full gRPC connection.
func NewManager(registration pb.RegistrationClient, configuration pb.ConfigurationClient, store *db.Store, runtimeState *state.RuntimeState, registrationToken string) *Manager {
	return &Manager{
		registration:      registration,
		configuration:     configuration,
		store:             store,
		state:             runtimeState,
		registrationToken: registrationToken,
	}
}

// EnsureRegistered checks if the agent is already registered, and if not,
// performs the registration flow. Returns immediately after successful registration.
func (m *Manager) EnsureRegistered(ctx context.Context) error {
	// Try to load registration token from store if not provided via config.
	if m.registrationToken == "" {
		storedToken, err := m.store.GetConfig("registration_token")
		if err != nil {
			slog.Warn("failed to read registration_token from config", "error", err)
		}
		if storedToken != "" {
			m.registrationToken = storedToken
		}
	}

	// Check for existing machine_id in database.
	machineIDStr, err := m.store.GetConfig("machine_id")
	if err != nil {
		return fmt.Errorf("reading machine_id from config: %w", err)
	}

	if machineIDStr != "" {
		machineID, err := strconv.ParseInt(machineIDStr, 10, 64)
		if err != nil {
			return fmt.Errorf("parsing stored machine_id: %w", err)
		}
		m.state.SetMachineID(machineID)
		m.state.SetRegistered(true)

		// Load API key from store.
		apiKey, err := m.store.GetConfig("api_key")
		if err != nil {
			slog.Warn("failed to read api_key from config", "error", err)
		}
		if apiKey != "" {
			m.state.SetApiKey(apiKey)
		} else {
			// Recovery: existing agent upgraded without stored API key.
			// Re-fetch from server via GetRegistrationStatus.
			slog.Info("no api_key stored, attempting recovery via GetRegistrationStatus", "machine_id", machineID)
			serialNumber, _ := m.store.GetConfig("serial_number")
			if err := m.recoverApiKey(ctx, serialNumber); err != nil {
				slog.Warn("failed to recover api_key, agent will not authenticate", "error", err)
			}
		}

		slog.Info("agent already registered", "machine_id", machineID)

		return nil
	}

	// New registration.
	return m.register(ctx)
}

func (m *Manager) register(ctx context.Context) error {
	hostname, _ := os.Hostname()
	m.state.SetHostname(hostname)

	serialNumber := detectSerial()

	// If no serial could be detected, use or generate a persistent one.
	if serialNumber == "" {
		stored, err := m.store.GetConfig("generated_serial")
		if err != nil {
			slog.Warn("failed to read generated_serial from config", "error", err)
		}
		if stored != "" {
			serialNumber = stored
		} else {
			serialNumber = "gen-" + uuid.New().String()
			if err := m.store.SetConfig("generated_serial", serialNumber); err != nil {
				slog.Warn("failed to store generated_serial", "error", err)
			}
		}
		slog.Info("using generated serial number", "serial", serialNumber)
	}

	m.state.SetSerialNumber(serialNumber)

	machineType := detectMachineType()
	osType := detectOSType()
	osVersion := detectOSVersion()
	systemID := readMachineID()

	slog.Info("registering system",
		"hostname", hostname,
		"serial_number", serialNumber,
		"machine_type", machineType,
		"os", osType,
	)

	// Check if this machine is already registered on the server (re-install scenario).
	if m.registrationToken != "" {
		statusResp, statusErr := m.registration.GetRegistrationStatus(ctx, &pb.SystemRegistrationStatusRequest{
			SerialNumber:      serialNumber,
			SystemId:          systemID,
			RegistrationToken: m.registrationToken,
			NeedsApiKey:       true,
		})
		if (statusErr == nil) && (statusResp.Status == pb.RegistrationStatus_REGISTRATION_ACTIVE) && (statusResp.MachineId > 0) {
			slog.Info("machine already registered on server, recovering", "machine_id", statusResp.MachineId)

			m.state.SetMachineID(statusResp.MachineId)
			m.state.SetRegistered(true)

			if err := m.store.SetConfig("machine_id", strconv.FormatInt(statusResp.MachineId, 10)); err != nil {
				return fmt.Errorf("storing recovered machine_id: %w", err)
			}
			if err := m.store.SetConfig("serial_number", serialNumber); err != nil {
				return fmt.Errorf("storing serial_number: %w", err)
			}
			if err := m.store.SetConfig("registration_token", m.registrationToken); err != nil {
				return fmt.Errorf("storing registration_token: %w", err)
			}

			if apiKey := statusResp.GetApiKey(); apiKey != "" {
				m.state.SetApiKey(apiKey)
				if err := m.store.SetConfig("api_key", apiKey); err != nil {
					return fmt.Errorf("storing recovered api_key: %w", err)
				}
			} else {
				slog.Warn("recovered machine_id but no api_key available from server")
			}

			slog.Info("registration recovery complete", "machine_id", statusResp.MachineId)

			return nil
		}
	}

	resp, err := m.registration.RegisterSystem(ctx, &pb.RegisterSystemRequest{
		Hostname:          hostname,
		SerialNumber:      serialNumber,
		SystemId:          systemID,
		AssetTag:          readDMIField("chassis_asset_tag"),
		MachineType:       machineType,
		Os:                osType,
		OsVersion:         osVersion,
		RegistrationToken: m.registrationToken,
	})
	if err != nil {
		return fmt.Errorf("RegisterSystem RPC: %w", err)
	}

	if resp.ErrorMessage != "" {
		return fmt.Errorf("registration error: %s", resp.ErrorMessage)
	}

	// Auto-enroll: machine_id and api_key are returned immediately.
	machineID := resp.MachineId
	apiKey := resp.ApiKey

	m.state.SetMachineID(machineID)
	m.state.SetRegistered(true)
	m.state.SetApiKey(apiKey)

	if err := m.store.SetConfig("machine_id", strconv.FormatInt(machineID, 10)); err != nil {
		return fmt.Errorf("storing machine_id: %w", err)
	}
	if err := m.store.SetConfig("api_key", apiKey); err != nil {
		return fmt.Errorf("storing api_key: %w", err)
	}
	if err := m.store.SetConfig("serial_number", serialNumber); err != nil {
		return fmt.Errorf("storing serial_number: %w", err)
	}
	if err := m.store.SetConfig("registration_token", m.registrationToken); err != nil {
		return fmt.Errorf("storing registration_token: %w", err)
	}

	slog.Info("registration complete",
		"machine_id", machineID,
	)

	return nil
}

// recoverApiKey attempts to retrieve the API key from the server for an already-registered agent
// that was upgraded before API key storage was implemented.
func (m *Manager) recoverApiKey(ctx context.Context, serialNumber string) error {
	systemID := readMachineID()
	resp, err := m.registration.GetRegistrationStatus(ctx, &pb.SystemRegistrationStatusRequest{
		SerialNumber:      serialNumber,
		SystemId:          systemID,
		RegistrationToken: m.registrationToken,
		NeedsApiKey:       true,
	})
	if err != nil {
		return fmt.Errorf("GetRegistrationStatus RPC: %w", err)
	}

	if apiKey := resp.GetApiKey(); apiKey != "" {
		m.state.SetApiKey(apiKey)
		if err := m.store.SetConfig("api_key", apiKey); err != nil {
			return fmt.Errorf("storing recovered api_key: %w", err)
		}
		slog.Info("recovered api_key from server")
	} else {
		return fmt.Errorf("server did not return an api_key")
	}
	return nil
}

// FetchConfiguration retrieves and stores the server configuration.
func (m *Manager) FetchConfiguration(ctx context.Context) error {
	machineID := m.state.MachineID()
	if machineID == 0 {
		return fmt.Errorf("not registered")
	}

	resp, err := m.configuration.GetConfiguration(ctx, &pb.GetConfigurationRequest{
		MachineId:         machineID,
		AgentCapabilities: m.state.AgentCapabilities(),
	})
	if err != nil {
		return fmt.Errorf("GetConfiguration RPC: %w", err)
	}

	// Apply timing config with bounds to prevent server-supplied DoS or misconfiguration.
	if tc := resp.TimeConfig; tc != nil {
		if (tc.HeartbeatTimeInSeconds >= 10) && (tc.HeartbeatTimeInSeconds <= 600) {
			m.state.SetPingInterval(time.Duration(tc.HeartbeatTimeInSeconds) * time.Second)
		}
		if (tc.ConfigurationRefreshTimeInSeconds >= 60) && (tc.ConfigurationRefreshTimeInSeconds <= 86400) {
			m.state.SetConfigRefreshInterval(time.Duration(tc.ConfigurationRefreshTimeInSeconds) * time.Second)
		}
		if (tc.CommandPollTimeInSeconds >= 10) && (tc.CommandPollTimeInSeconds <= 300) {
			m.state.SetCommandPollInterval(time.Duration(tc.CommandPollTimeInSeconds) * time.Second)
		}
		if (tc.TelemetryCollectFastSeconds >= 10) && (tc.TelemetryCollectFastSeconds <= 300) {
			m.state.SetTelemetryCollectFastInterval(time.Duration(tc.TelemetryCollectFastSeconds) * time.Second)
		}
		if (tc.TelemetryCollectSlowSeconds >= 60) && (tc.TelemetryCollectSlowSeconds <= 3600) {
			m.state.SetTelemetryCollectSlowInterval(time.Duration(tc.TelemetryCollectSlowSeconds) * time.Second)
		}
		if (tc.TelemetrySendFastSeconds >= 5) && (tc.TelemetrySendFastSeconds <= 120) {
			m.state.SetTelemetrySendFastInterval(time.Duration(tc.TelemetrySendFastSeconds) * time.Second)
		}
		if (tc.TelemetrySendSlowSeconds >= 30) && (tc.TelemetrySendSlowSeconds <= 1800) {
			m.state.SetTelemetrySendSlowInterval(time.Duration(tc.TelemetrySendSlowSeconds) * time.Second)
		}
		if (tc.ServiceStatusSeconds >= 60) && (tc.ServiceStatusSeconds <= 86400) {
			m.state.SetServiceStatusInterval(time.Duration(tc.ServiceStatusSeconds) * time.Second)
		}
	}

	// Store tenant ID so the agent can verify command ownership.
	if resp.TenantId > 0 {
		m.state.SetTenantID(resp.TenantId)
	}

	// Sync trusted signing keys for remote command verification.
	if len(resp.SigningKeys) > 0 {
		keys := make([]db.TrustedKey, 0, len(resp.SigningKeys))
		for _, sk := range resp.SigningKeys {
			keys = append(keys, db.TrustedKey{
				KeyID:     sk.KeyId,
				UserID:    sk.UserId,
				PublicKey: sk.PublicKey,
			})
		}
		if err := m.store.UpsertSigningKeys(keys); err != nil {
			slog.Warn("failed to sync signing keys", "error", err)
		} else {
			slog.Debug("synced signing keys", "count", len(keys))
		}
	}

	// Dynamic API key rotation: if the server provides a new key, swap to it.
	if newApiKey := resp.GetApiKey(); newApiKey != "" {
		slog.Info("server issued rotated API key, applying")
		m.state.SetApiKey(newApiKey)
		if err := m.store.SetConfig("api_key", newApiKey); err != nil {
			slog.Warn("failed to persist rotated api_key", "error", err)
		}
	}

	// Store full config as JSON.
	configJSON, err := json.Marshal(resp)
	if err != nil {
		return fmt.Errorf("marshaling config: %w", err)
	}

	return m.store.SaveServerConfig(string(configJSON))
}

// Ping sends a heartbeat to the server.
func (m *Manager) Ping(ctx context.Context) error {
	machineID := m.state.MachineID()
	if machineID == 0 {
		return fmt.Errorf("not registered")
	}

	resp, err := m.configuration.AgentPing(ctx, &pb.AgentPingRequest{
		MachineId: machineID,
	})
	if err != nil {
		return fmt.Errorf("AgentPing RPC: %w", err)
	}
	if resp.Success == false {

		return fmt.Errorf("ping unsuccessful")
	}

	return nil
}

func readDMIField(field string) string {
	data, err := os.ReadFile("/sys/class/dmi/id/" + filepath.Base(field))
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(data))
}

func readMachineID() string {
	data, err := os.ReadFile("/etc/machine-id")
	if err != nil {
		return ""
	}
	return strings.TrimSpace(string(data))
}

func detectSerial() string {
	serial := readDMIField("product_serial")
	if serial != "" && serial != "Not Specified" && serial != "To Be Filled By O.E.M." {
		return serial
	}

	// Fallback to board serial.
	serial = readDMIField("board_serial")
	if serial != "" && serial != "Not Specified" && serial != "To Be Filled By O.E.M." {
		return serial
	}

	// Fallback to /etc/machine-id (systemd distros).
	if mid := readMachineIDSerial("/etc/machine-id"); mid != "" {
		return mid
	}

	// Fallback to /var/lib/dbus/machine-id (older dbus systems).
	if mid := readMachineIDSerial("/var/lib/dbus/machine-id"); mid != "" {
		return mid
	}

	return ""
}

// readMachineIDSerial reads a machine-id file and returns a prefixed serial.
func readMachineIDSerial(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	mid := strings.TrimSpace(string(data))
	if mid == "" {
		return ""
	}
	// Use first 12 chars with a prefix to distinguish from hardware serials.
	if len(mid) > 12 {
		mid = mid[:12]
	}

	return "mid-" + mid
}

func detectMachineType() pb.MachineType {
	productName := strings.ToLower(readDMIField("product_name"))

	// Check for virtual machine.
	vmIndicators := []string{"kvm", "qemu", "vmware", "virtualbox", "xen", "hyper-v", "bochs"}
	for _, indicator := range vmIndicators {
		if strings.Contains(productName, indicator) {
			return pb.MachineType_VIRTUAL_MACHINE_TYPE
		}
	}

	// Check chassis type (3=Desktop, 9/10=Laptop, 17=Server).
	chassisType := readDMIField("chassis_type")
	switch chassisType {
	case "3", "4", "5", "6", "7", "24":
		return pb.MachineType_DESKTOP_TYPE
	case "8", "9", "10", "14", "31", "32":
		return pb.MachineType_LAPTOP_TYPE
	case "17", "23", "25", "28", "29":
		return pb.MachineType_BARE_METAL_SERVER_TYPE
	}

	return pb.MachineType_UNKNOWN_TYPE
}

func detectOSType() pb.OperatingSystemType {
	data, err := os.ReadFile("/etc/os-release")
	if err != nil {
		return pb.OperatingSystemType_UNKNOWN_OS
	}

	content := strings.ToLower(string(data))

	// Check specific distros — order matters: check more specific IDs before broader ones.
	if strings.Contains(content, "ubuntu") {
		return pb.OperatingSystemType_UBUNTU_OS
	}
	if strings.Contains(content, "id=debian") || strings.Contains(content, "id=\"debian\"") {
		return pb.OperatingSystemType_DEBIAN_OS
	}
	if strings.Contains(content, "fedora") {
		return pb.OperatingSystemType_FEDORA_OS
	}
	if strings.Contains(content, "rhel") || strings.Contains(content, "red hat") {
		return pb.OperatingSystemType_REDHAT_OS
	}
	if strings.Contains(content, "centos") || strings.Contains(content, "rocky") ||
		strings.Contains(content, "alma") || strings.Contains(content, "oracle linux") {
		return pb.OperatingSystemType_REDHAT_OS // RHEL-derivative distros
	}
	if strings.Contains(content, "suse") || strings.Contains(content, "opensuse") {
		return pb.OperatingSystemType_UNKNOWN_OS // No SUSE enum yet — future proto update
	}
	if strings.Contains(content, "alpine") {
		return pb.OperatingSystemType_UNKNOWN_OS // No Alpine enum yet — future proto update
	}
	if strings.Contains(content, "arch") {
		return pb.OperatingSystemType_UNKNOWN_OS // No Arch enum yet — future proto update
	}

	return pb.OperatingSystemType_UNKNOWN_OS
}

func detectOSVersion() string {
	data, err := os.ReadFile("/etc/os-release")
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(string(data), "\n") {
		if strings.HasPrefix(line, "VERSION_ID=") {
			return strings.Trim(strings.TrimPrefix(line, "VERSION_ID="), "\"")
		}
	}
	return ""
}
