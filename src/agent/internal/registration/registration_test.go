// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package registration

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"google.golang.org/grpc"

	"github.com/framlux/vord/internal/db"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
)

// --- Mock implementations ---

type mockRegistrationClient struct {
	registerFunc func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error)
	statusFunc   func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error)
}

func (m *mockRegistrationClient) RegisterSystem(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
	if m.registerFunc != nil {
		return m.registerFunc(ctx, in, opts...)
	}

	return &pb.RegisterSystemResponse{MachineId: 1, ApiKey: "test-key"}, nil
}

func (m *mockRegistrationClient) GetRegistrationStatus(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
	if m.statusFunc != nil {
		return m.statusFunc(ctx, in, opts...)
	}

	return &pb.SystemRegistrationStatusResponse{ApiKey: "recovered-key"}, nil
}

type mockConfigurationClient struct {
	getConfigFunc func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error)
	pingFunc      func(ctx context.Context, in *pb.AgentPingRequest, opts ...grpc.CallOption) (*pb.AgentPingResponse, error)
	commandsFunc  func(ctx context.Context, in *pb.GetPendingCommandsRequest, opts ...grpc.CallOption) (*pb.GetPendingCommandsResponse, error)
}

func (m *mockConfigurationClient) GetConfiguration(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
	if m.getConfigFunc != nil {
		return m.getConfigFunc(ctx, in, opts...)
	}

	return &pb.GetConfigurationResponse{}, nil
}

func (m *mockConfigurationClient) AgentPing(ctx context.Context, in *pb.AgentPingRequest, opts ...grpc.CallOption) (*pb.AgentPingResponse, error) {
	if m.pingFunc != nil {
		return m.pingFunc(ctx, in, opts...)
	}

	return &pb.AgentPingResponse{Success: true}, nil
}

func (m *mockConfigurationClient) GetPendingCommands(ctx context.Context, in *pb.GetPendingCommandsRequest, opts ...grpc.CallOption) (*pb.GetPendingCommandsResponse, error) {
	if m.commandsFunc != nil {
		return m.commandsFunc(ctx, in, opts...)
	}

	return &pb.GetPendingCommandsResponse{}, nil
}

func (m *mockConfigurationClient) AcknowledgeCommand(ctx context.Context, in *pb.AcknowledgeCommandRequest, opts ...grpc.CallOption) (*pb.AcknowledgeCommandResponse, error) {
	return &pb.AcknowledgeCommandResponse{Success: true}, nil
}

// --- Test helpers ---

func newTestStore(t *testing.T) *db.Store {
	t.Helper()
	database, err := db.Open(":memory:")
	if err != nil {
		t.Fatalf("open test db: %v", err)
	}
	t.Cleanup(func() { database.Close() })

	return db.NewStore(database)
}

// newTestManager creates a Manager wired to mock gRPC clients for testing.
func newTestManager(reg pb.RegistrationClient, cfg pb.ConfigurationClient, store *db.Store, rs *state.RuntimeState, token string) *Manager {
	return NewManager(reg, cfg, store, rs, token)
}

// --- EnsureRegistered tests ---

// Intent: If machine_id exists in DB with API key, no RPC is made.
func TestEnsureRegistered_AlreadyRegistered(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	// Pre-populate database with existing registration.
	if err := store.SetConfig("machine_id", "42"); err != nil {
		t.Fatalf("SetConfig machine_id: %v", err)
	}
	if err := store.SetConfig("api_key", "existing-key"); err != nil {
		t.Fatalf("SetConfig api_key: %v", err)
	}

	rpcCalled := false
	regClient := &mockRegistrationClient{
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			rpcCalled = true

			return nil, fmt.Errorf("should not be called")
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if rpcCalled {
		t.Error("expected no RPC call when already registered")
	}
	if runtimeState.MachineID() != 42 {
		t.Errorf("expected MachineID=42, got %d", runtimeState.MachineID())
	}
	if runtimeState.IsRegistered() == false {
		t.Error("expected IsRegistered=true")
	}
	if runtimeState.ApiKey() != "existing-key" {
		t.Errorf("expected ApiKey=%q, got %q", "existing-key", runtimeState.ApiKey())
	}
}

// Intent: machine_id exists but no API key → calls recoverApiKey via GetRegistrationStatus.
func TestEnsureRegistered_MissingApiKey(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	if err := store.SetConfig("machine_id", "42"); err != nil {
		t.Fatalf("SetConfig machine_id: %v", err)
	}
	// No api_key set — simulates upgrade scenario.

	statusCalled := false
	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			statusCalled = true

			return &pb.SystemRegistrationStatusResponse{ApiKey: "recovered-key-123"}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if statusCalled == false {
		t.Error("expected GetRegistrationStatus to be called for API key recovery")
	}
	if runtimeState.ApiKey() != "recovered-key-123" {
		t.Errorf("expected ApiKey=%q, got %q", "recovered-key-123", runtimeState.ApiKey())
	}

	// API key should also be stored in database.
	storedKey, err := store.GetConfig("api_key")
	if err != nil {
		t.Fatalf("GetConfig api_key: %v", err)
	}
	if storedKey != "recovered-key-123" {
		t.Errorf("expected stored api_key=%q, got %q", "recovered-key-123", storedKey)
	}
}

// Intent: Server doesn't recognize machine during API key recovery → returns error.
func TestRecoverApiKey_ServerRejectsUnknown(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	if err := store.SetConfig("machine_id", "42"); err != nil {
		t.Fatalf("SetConfig machine_id: %v", err)
	}
	// No api_key set.

	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			// Server returns response with empty API key.
			return &pb.SystemRegistrationStatusResponse{ApiKey: ""}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	// EnsureRegistered should complete (it warns but doesn't fail for API key recovery).
	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	// API key should remain empty.
	if runtimeState.ApiKey() != "" {
		t.Errorf("expected empty ApiKey after failed recovery, got %q", runtimeState.ApiKey())
	}
}

// Intent: Server returns registration_token from store when not provided via config.
func TestEnsureRegistered_LoadsTokenFromStore(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	if err := store.SetConfig("registration_token", "stored-token"); err != nil {
		t.Fatalf("SetConfig registration_token: %v", err)
	}
	if err := store.SetConfig("machine_id", "99"); err != nil {
		t.Fatalf("SetConfig machine_id: %v", err)
	}
	if err := store.SetConfig("api_key", "key"); err != nil {
		t.Fatalf("SetConfig api_key: %v", err)
	}

	regClient := &mockRegistrationClient{}
	cfgClient := &mockConfigurationClient{}

	// Empty registration token — should load from store.
	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if mgr.registrationToken != "stored-token" {
		t.Errorf("expected registration token %q loaded from store, got %q", "stored-token", mgr.registrationToken)
	}
}

// --- Register (new machine) tests ---

// Intent: No machine_id in DB → calls RegisterSystem RPC, stores machine_id, api_key, serial.
func TestEnsureRegistered_NewMachine(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	registerCalled := false
	regClient := &mockRegistrationClient{
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			registerCalled = true
			if in.RegistrationToken != "my-token" {
				t.Errorf("expected RegistrationToken=%q, got %q", "my-token", in.RegistrationToken)
			}

			return &pb.RegisterSystemResponse{
				MachineId: 42,
				ApiKey:    "new-api-key",
			}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "my-token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if registerCalled == false {
		t.Error("expected RegisterSystem RPC to be called")
	}
	if runtimeState.MachineID() != 42 {
		t.Errorf("expected MachineID=42, got %d", runtimeState.MachineID())
	}
	if runtimeState.IsRegistered() == false {
		t.Error("expected IsRegistered=true")
	}
	if runtimeState.ApiKey() != "new-api-key" {
		t.Errorf("expected ApiKey=%q, got %q", "new-api-key", runtimeState.ApiKey())
	}

	// Verify state persisted to DB.
	storedID, err := store.GetConfig("machine_id")
	if err != nil {
		t.Fatalf("GetConfig machine_id: %v", err)
	}
	if storedID != "42" {
		t.Errorf("expected stored machine_id=%q, got %q", "42", storedID)
	}

	storedKey, err := store.GetConfig("api_key")
	if err != nil {
		t.Fatalf("GetConfig api_key: %v", err)
	}
	if storedKey != "new-api-key" {
		t.Errorf("expected stored api_key=%q, got %q", "new-api-key", storedKey)
	}

	storedToken, err := store.GetConfig("registration_token")
	if err != nil {
		t.Fatalf("GetConfig registration_token: %v", err)
	}
	if storedToken != "my-token" {
		t.Errorf("expected stored registration_token=%q, got %q", "my-token", storedToken)
	}
}

// Intent: RegisterSystem RPC returns error → EnsureRegistered propagates the error.
func TestEnsureRegistered_RPCError(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	regClient := &mockRegistrationClient{
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			return nil, fmt.Errorf("connection refused")
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.EnsureRegistered(context.Background())
	if err == nil {
		t.Fatal("expected error from failed RegisterSystem RPC")
	}
	if runtimeState.IsRegistered() {
		t.Error("expected IsRegistered=false after RPC failure")
	}
}

// Intent: Server returns error message in response → treated as registration error.
func TestEnsureRegistered_ServerRejectsRegistration(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	regClient := &mockRegistrationClient{
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			return &pb.RegisterSystemResponse{
				ErrorMessage: "tenant registration limit reached",
			}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.EnsureRegistered(context.Background())
	if err == nil {
		t.Fatal("expected error when server returns error message")
	}
	if runtimeState.IsRegistered() {
		t.Error("expected IsRegistered=false after server rejection")
	}
}

// --- FetchConfiguration tests ---

// Intent: Server returns heartbeat < 10s → clamped (not applied).
func TestFetchConfiguration_EnforcesMinHeartbeat(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            5, // < 10, should be rejected
					ConfigurationRefreshTimeInSeconds: 60,
				},
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	// Heartbeat should remain at default (60s), not be set to 5s.
	if runtimeState.PingInterval() != 60*time.Second {
		t.Errorf("expected PingInterval=60s (default, since 5s < 10s), got %v", runtimeState.PingInterval())
	}
	// Config refresh should be applied (60s >= 30s).
	if runtimeState.ConfigRefreshInterval() != 60*time.Second {
		t.Errorf("expected ConfigRefreshInterval=60s, got %v", runtimeState.ConfigRefreshInterval())
	}
}

// Intent: Server returns config refresh < 30s → clamped (not applied).
func TestFetchConfiguration_EnforcesMinConfigRefresh(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            30,
					ConfigurationRefreshTimeInSeconds: 10, // < 30, should be rejected
				},
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	// Heartbeat should be applied (30s >= 10s).
	if runtimeState.PingInterval() != 30*time.Second {
		t.Errorf("expected PingInterval=30s, got %v", runtimeState.PingInterval())
	}
	// Config refresh should remain at default (5m), not be set to 10s.
	if runtimeState.ConfigRefreshInterval() != 5*time.Minute {
		t.Errorf("expected ConfigRefreshInterval=5m (default, since 10s < 30s), got %v", runtimeState.ConfigRefreshInterval())
	}
}

// Intent: Valid server response updates RuntimeState correctly.
func TestFetchConfiguration_ValidConfig(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            120,
					ConfigurationRefreshTimeInSeconds: 300,
				},
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	if runtimeState.PingInterval() != 120*time.Second {
		t.Errorf("expected PingInterval=120s, got %v", runtimeState.PingInterval())
	}
	if runtimeState.ConfigRefreshInterval() != 300*time.Second {
		t.Errorf("expected ConfigRefreshInterval=300s, got %v", runtimeState.ConfigRefreshInterval())
	}
}

// --- Ping tests ---

// Intent: Ping before registration (machineID=0) returns error.
func TestPing_NotRegistered(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New() // machineID=0

	regClient := &mockRegistrationClient{}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.Ping(context.Background())
	if err == nil {
		t.Error("expected error for ping when not registered")
	}
}

// Intent: Server rejects heartbeat → returns error.
func TestPing_ServerReject(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		pingFunc: func(ctx context.Context, in *pb.AgentPingRequest, opts ...grpc.CallOption) (*pb.AgentPingResponse, error) {
			return &pb.AgentPingResponse{Success: false}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.Ping(context.Background())
	if err == nil {
		t.Error("expected error for rejected ping")
	}
}

// Intent: Server accepts heartbeat → no error.
func TestPing_Success(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		pingFunc: func(ctx context.Context, in *pb.AgentPingRequest, opts ...grpc.CallOption) (*pb.AgentPingResponse, error) {
			if in.MachineId != 1 {
				t.Errorf("expected MachineId=1 in ping request, got %d", in.MachineId)
			}

			return &pb.AgentPingResponse{Success: true}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.Ping(context.Background())
	if err != nil {
		t.Fatalf("Ping: %v", err)
	}
}

// Intent: FetchConfiguration when not registered (machineID=0) returns error.
func TestFetchConfiguration_NotRegistered(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New() // machineID=0

	regClient := &mockRegistrationClient{}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err == nil {
		t.Error("expected error for FetchConfiguration when not registered")
	}
}

// --- readMachineIDSerial tests ---

// Intent: Reads file, prefixes "mid-", truncates to 12 chars.
func TestReadMachineIDSerial_ValidFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "machine-id")
	if err := os.WriteFile(path, []byte("abcdef1234567890\n"), 0644); err != nil {
		t.Fatalf("write file: %v", err)
	}

	result := readMachineIDSerial(path)

	if result != "mid-abcdef123456" {
		t.Errorf("expected %q, got %q", "mid-abcdef123456", result)
	}
}

// Intent: Short machine-id uses full content.
func TestReadMachineIDSerial_ShortID(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "machine-id")
	if err := os.WriteFile(path, []byte("abc123\n"), 0644); err != nil {
		t.Fatalf("write file: %v", err)
	}

	result := readMachineIDSerial(path)

	if result != "mid-abc123" {
		t.Errorf("expected %q, got %q", "mid-abc123", result)
	}
}

// Intent: Missing file returns empty string (not error).
func TestReadMachineIDSerial_MissingFile(t *testing.T) {
	result := readMachineIDSerial("/nonexistent/path/machine-id")

	if result != "" {
		t.Errorf("expected empty string for missing file, got %q", result)
	}
}

// Intent: Empty file returns empty string.
func TestReadMachineIDSerial_EmptyFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "machine-id")
	if err := os.WriteFile(path, []byte(""), 0644); err != nil {
		t.Fatalf("write file: %v", err)
	}

	result := readMachineIDSerial(path)

	if result != "" {
		t.Errorf("expected empty string for empty file, got %q", result)
	}
}
