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
			if in.NeedsApiKey == false {
				t.Error("expected NeedsApiKey=true for API key recovery")
			}

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

// --- Re-registration recovery tests ---

// Intent: Agent has no local state but machine exists on server → recovers via GetRegistrationStatus
// instead of failing with "Machine already exists".
func TestEnsureRegistered_ReinstallRecovery(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	// No machine_id or api_key in store — simulates fresh agent install on a previously-registered machine.

	statusCalled := false
	registerCalled := false
	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			statusCalled = true
			if in.RegistrationToken != "my-token" {
				t.Errorf("expected RegistrationToken=%q, got %q", "my-token", in.RegistrationToken)
			}
			if in.NeedsApiKey == false {
				t.Error("expected NeedsApiKey=true for reinstall recovery")
			}

			return &pb.SystemRegistrationStatusResponse{
				Status:    pb.RegistrationStatus_REGISTRATION_ACTIVE,
				MachineId: 77,
				ApiKey:    "recovered-api-key",
			}, nil
		},
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			registerCalled = true

			return nil, fmt.Errorf("should not be called")
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "my-token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if statusCalled == false {
		t.Error("expected GetRegistrationStatus to be called for re-install recovery")
	}
	if registerCalled {
		t.Error("expected RegisterSystem NOT to be called when recovery succeeds")
	}
	if runtimeState.MachineID() != 77 {
		t.Errorf("expected MachineID=77, got %d", runtimeState.MachineID())
	}
	if runtimeState.IsRegistered() == false {
		t.Error("expected IsRegistered=true")
	}
	if runtimeState.ApiKey() != "recovered-api-key" {
		t.Errorf("expected ApiKey=%q, got %q", "recovered-api-key", runtimeState.ApiKey())
	}

	// Verify state persisted to DB.
	storedID, err := store.GetConfig("machine_id")
	if err != nil {
		t.Fatalf("GetConfig machine_id: %v", err)
	}
	if storedID != "77" {
		t.Errorf("expected stored machine_id=%q, got %q", "77", storedID)
	}

	storedKey, err := store.GetConfig("api_key")
	if err != nil {
		t.Fatalf("GetConfig api_key: %v", err)
	}
	if storedKey != "recovered-api-key" {
		t.Errorf("expected stored api_key=%q, got %q", "recovered-api-key", storedKey)
	}
}

// Intent: GetRegistrationStatus returns UNKNOWN → falls through to RegisterSystem normally.
func TestEnsureRegistered_RecoveryMiss_FallsToRegister(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	statusCalled := false
	registerCalled := false
	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			statusCalled = true

			return &pb.SystemRegistrationStatusResponse{
				Status: pb.RegistrationStatus_UNKNOWN_REGISTRATION,
			}, nil
		},
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			registerCalled = true

			return &pb.RegisterSystemResponse{
				MachineId: 99,
				ApiKey:    "fresh-key",
			}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "my-token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if statusCalled == false {
		t.Error("expected GetRegistrationStatus to be called first")
	}
	if registerCalled == false {
		t.Error("expected RegisterSystem to be called after status miss")
	}
	if runtimeState.MachineID() != 99 {
		t.Errorf("expected MachineID=99, got %d", runtimeState.MachineID())
	}
}

// Intent: GetRegistrationStatus RPC fails → falls through to RegisterSystem gracefully.
func TestEnsureRegistered_RecoveryRPCError_FallsToRegister(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	registerCalled := false
	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			return nil, fmt.Errorf("network timeout")
		},
		registerFunc: func(ctx context.Context, in *pb.RegisterSystemRequest, opts ...grpc.CallOption) (*pb.RegisterSystemResponse, error) {
			registerCalled = true

			return &pb.RegisterSystemResponse{
				MachineId: 50,
				ApiKey:    "new-key",
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
		t.Error("expected RegisterSystem to be called after GetRegistrationStatus failure")
	}
	if runtimeState.MachineID() != 50 {
		t.Errorf("expected MachineID=50, got %d", runtimeState.MachineID())
	}
}

// Intent: Recovery returns machine_id but no api_key → machine_id is stored, api_key remains empty.
func TestEnsureRegistered_RecoveryNoApiKey(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()

	regClient := &mockRegistrationClient{
		statusFunc: func(ctx context.Context, in *pb.SystemRegistrationStatusRequest, opts ...grpc.CallOption) (*pb.SystemRegistrationStatusResponse, error) {
			return &pb.SystemRegistrationStatusResponse{
				Status:    pb.RegistrationStatus_REGISTRATION_ACTIVE,
				MachineId: 77,
				ApiKey:    "", // API key cache expired on server
			}, nil
		},
	}
	cfgClient := &mockConfigurationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "my-token")

	err := mgr.EnsureRegistered(context.Background())
	if err != nil {
		t.Fatalf("EnsureRegistered: %v", err)
	}

	if runtimeState.MachineID() != 77 {
		t.Errorf("expected MachineID=77, got %d", runtimeState.MachineID())
	}
	if runtimeState.IsRegistered() == false {
		t.Error("expected IsRegistered=true")
	}
	if runtimeState.ApiKey() != "" {
		t.Errorf("expected empty ApiKey, got %q", runtimeState.ApiKey())
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

// Intent: Heartbeat above upper bound (600s) is rejected.
func TestFetchConfiguration_EnforcesMaxHeartbeat(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds: 1000, // > 600, should be rejected
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

	if runtimeState.PingInterval() != 60*time.Second {
		t.Errorf("expected PingInterval=60s (default, since 1000 > 600), got %v", runtimeState.PingInterval())
	}
}

// Intent: Config refresh above upper bound (86400s) is rejected.
func TestFetchConfiguration_EnforcesMaxConfigRefresh(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ConfigurationRefreshTimeInSeconds: 100000, // > 86400, should be rejected
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

	if runtimeState.ConfigRefreshInterval() != 5*time.Minute {
		t.Errorf("expected ConfigRefreshInterval=5m (default, since 100000 > 86400), got %v", runtimeState.ConfigRefreshInterval())
	}
}

// Intent: Telemetry collect fast below minimum (10s) is rejected.
func TestFetchConfiguration_EnforcesMinTelemetryCollectFast(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					TelemetryCollectFastSeconds: 5, // < 10, should be rejected
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

	if runtimeState.TelemetryCollectFastInterval() != 60*time.Second {
		t.Errorf("expected TelemetryCollectFastInterval=60s (default), got %v", runtimeState.TelemetryCollectFastInterval())
	}
}

// Intent: Telemetry collect fast above max (300s) is rejected.
func TestFetchConfiguration_EnforcesMaxTelemetryCollectFast(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					TelemetryCollectFastSeconds: 500, // > 300, should be rejected
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

	if runtimeState.TelemetryCollectFastInterval() != 60*time.Second {
		t.Errorf("expected TelemetryCollectFastInterval=60s (default), got %v", runtimeState.TelemetryCollectFastInterval())
	}
}

// Intent: Valid telemetry timing values are applied correctly.
func TestFetchConfiguration_ValidTelemetryConfig(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					TelemetryCollectFastSeconds: 60,
					TelemetryCollectSlowSeconds: 1800,
					TelemetrySendFastSeconds:    10,
					TelemetrySendSlowSeconds:    600,
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

	if runtimeState.TelemetryCollectFastInterval() != 60*time.Second {
		t.Errorf("expected TelemetryCollectFastInterval=60s, got %v", runtimeState.TelemetryCollectFastInterval())
	}
	if runtimeState.TelemetryCollectSlowInterval() != 1800*time.Second {
		t.Errorf("expected TelemetryCollectSlowInterval=1800s, got %v", runtimeState.TelemetryCollectSlowInterval())
	}
	if runtimeState.TelemetrySendFastInterval() != 10*time.Second {
		t.Errorf("expected TelemetrySendFastInterval=10s, got %v", runtimeState.TelemetrySendFastInterval())
	}
	if runtimeState.TelemetrySendSlowInterval() != 600*time.Second {
		t.Errorf("expected TelemetrySendSlowInterval=600s, got %v", runtimeState.TelemetrySendSlowInterval())
	}
}

// Intent: Boundary values (exact min and max) are accepted.
func TestFetchConfiguration_TelemetryBoundaryValues(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            10,    // exact min
					ConfigurationRefreshTimeInSeconds: 86400, // exact max
					CommandPollTimeInSeconds:          300,   // exact max
					TelemetryCollectFastSeconds:       10,    // exact min
					TelemetryCollectSlowSeconds:       3600,  // exact max
					TelemetrySendFastSeconds:          5,     // exact min
					TelemetrySendSlowSeconds:          1800,  // exact max
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

	if runtimeState.PingInterval() != 10*time.Second {
		t.Errorf("expected PingInterval=10s (exact min), got %v", runtimeState.PingInterval())
	}
	if runtimeState.ConfigRefreshInterval() != 86400*time.Second {
		t.Errorf("expected ConfigRefreshInterval=86400s (exact max), got %v", runtimeState.ConfigRefreshInterval())
	}
	if runtimeState.CommandPollInterval() != 300*time.Second {
		t.Errorf("expected CommandPollInterval=300s (exact max), got %v", runtimeState.CommandPollInterval())
	}
	if runtimeState.TelemetryCollectFastInterval() != 10*time.Second {
		t.Errorf("expected TelemetryCollectFastInterval=10s (exact min), got %v", runtimeState.TelemetryCollectFastInterval())
	}
	if runtimeState.TelemetryCollectSlowInterval() != 3600*time.Second {
		t.Errorf("expected TelemetryCollectSlowInterval=3600s (exact max), got %v", runtimeState.TelemetryCollectSlowInterval())
	}
	if runtimeState.TelemetrySendFastInterval() != 5*time.Second {
		t.Errorf("expected TelemetrySendFastInterval=5s (exact min), got %v", runtimeState.TelemetrySendFastInterval())
	}
	if runtimeState.TelemetrySendSlowInterval() != 1800*time.Second {
		t.Errorf("expected TelemetrySendSlowInterval=1800s (exact max), got %v", runtimeState.TelemetrySendSlowInterval())
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

// --- Dynamic API key rotation tests ---

// Intent: Server includes a rotated API key in config response → agent updates state and store.
func TestFetchConfiguration_RotatesApiKey(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	// Pre-populate with an existing key.
	if err := store.SetConfig("api_key", "old-key"); err != nil {
		t.Fatalf("SetConfig api_key: %v", err)
	}
	runtimeState.SetApiKey("old-key")

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            30,
					ConfigurationRefreshTimeInSeconds: 300,
				},
				ApiKey: "rotated-key-456",
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	if runtimeState.ApiKey() != "rotated-key-456" {
		t.Errorf("expected ApiKey=%q after rotation, got %q", "rotated-key-456", runtimeState.ApiKey())
	}

	storedKey, err := store.GetConfig("api_key")
	if err != nil {
		t.Fatalf("GetConfig api_key: %v", err)
	}
	if storedKey != "rotated-key-456" {
		t.Errorf("expected stored api_key=%q, got %q", "rotated-key-456", storedKey)
	}
}

// Intent: Server returns empty api_key in config → agent keeps existing key unchanged.
func TestFetchConfiguration_EmptyApiKey_NoRotation(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	if err := store.SetConfig("api_key", "existing-key"); err != nil {
		t.Fatalf("SetConfig api_key: %v", err)
	}
	runtimeState.SetApiKey("existing-key")

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            30,
					ConfigurationRefreshTimeInSeconds: 300,
				},
				// ApiKey intentionally empty — no rotation.
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	if runtimeState.ApiKey() != "existing-key" {
		t.Errorf("expected ApiKey=%q unchanged, got %q", "existing-key", runtimeState.ApiKey())
	}
}

// Intent: FetchConfiguration sends the agent capabilities bitmask to the server.
func TestFetchConfiguration_SendsAgentCapabilities(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(42)
	runtimeState.SetAgentCapabilities(1) // bit 0 = remote commands

	var receivedCapabilities uint64
	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			receivedCapabilities = in.AgentCapabilities

			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            30,
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

	if receivedCapabilities != 1 {
		t.Errorf("expected AgentCapabilities=1, got %d", receivedCapabilities)
	}
}

// Intent: FetchConfiguration sends zero capabilities when none are set.
func TestFetchConfiguration_SendsZeroCapabilitiesWhenDisabled(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(42)
	// No capabilities set — default is 0.

	var receivedCapabilities uint64
	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			receivedCapabilities = in.AgentCapabilities

			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					HeartbeatTimeInSeconds:            30,
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

	if receivedCapabilities != 0 {
		t.Errorf("expected AgentCapabilities=0, got %d", receivedCapabilities)
	}
}

// --- ServiceStatusSeconds bounds-checking tests ---

// Intent: A valid ServiceStatusSeconds value (3600, mid-range) is applied to RuntimeState.
func TestFetchConfiguration_ServiceStatusSeconds_ValidRange_Applied(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 3600,
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

	if runtimeState.ServiceStatusInterval() != 3600*time.Second {
		t.Errorf("expected ServiceStatusInterval=3600s, got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds at the exact minimum boundary (60) is accepted.
func TestFetchConfiguration_ServiceStatusSeconds_MinBoundary_Applied(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 60,
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

	if runtimeState.ServiceStatusInterval() != 60*time.Second {
		t.Errorf("expected ServiceStatusInterval=60s (exact min), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds at the exact maximum boundary (86400) is accepted.
func TestFetchConfiguration_ServiceStatusSeconds_MaxBoundary_Applied(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 86400,
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

	if runtimeState.ServiceStatusInterval() != 86400*time.Second {
		t.Errorf("expected ServiceStatusInterval=86400s (exact max), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds one below the minimum (59) is rejected, leaving the default unchanged.
func TestFetchConfiguration_ServiceStatusSeconds_BelowMin_Ignored(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 59,
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

	// Default is 1 hour; 59 is below the 60s minimum so it must be rejected.
	if runtimeState.ServiceStatusInterval() != 1*time.Hour {
		t.Errorf("expected ServiceStatusInterval=1h (default, since 59 < 60), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds one above the maximum (86401) is rejected, leaving the default unchanged.
func TestFetchConfiguration_ServiceStatusSeconds_AboveMax_Ignored(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 86401,
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

	if runtimeState.ServiceStatusInterval() != 1*time.Hour {
		t.Errorf("expected ServiceStatusInterval=1h (default, since 86401 > 86400), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds=0 (proto3 default for unset int32 fields) is rejected,
// so an empty server response does not accidentally zero out the interval.
func TestFetchConfiguration_ServiceStatusSeconds_Zero_Ignored(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: 0,
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

	if runtimeState.ServiceStatusInterval() != 1*time.Hour {
		t.Errorf("expected ServiceStatusInterval=1h (default, since 0 < 60), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: ServiceStatusSeconds=-1 (negative value) is rejected so a malicious or
// buggy server cannot cause a negative ticker interval.
func TestFetchConfiguration_ServiceStatusSeconds_Negative_Ignored(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: &pb.TimingConfiguration{
					ServiceStatusSeconds: -1,
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

	if runtimeState.ServiceStatusInterval() != 1*time.Hour {
		t.Errorf("expected ServiceStatusInterval=1h (default, since -1 < 60), got %v", runtimeState.ServiceStatusInterval())
	}
}

// Intent: A nil TimeConfig in the response must not cause a panic — the agent
// should gracefully skip timing updates when the field is absent.
func TestFetchConfiguration_NilTimeConfig_DoesNotPanic(t *testing.T) {
	store := newTestStore(t)
	runtimeState := state.New()
	runtimeState.SetMachineID(1)

	cfgClient := &mockConfigurationClient{
		getConfigFunc: func(ctx context.Context, in *pb.GetConfigurationRequest, opts ...grpc.CallOption) (*pb.GetConfigurationResponse, error) {
			return &pb.GetConfigurationResponse{
				TimeConfig: nil,
			}, nil
		},
	}
	regClient := &mockRegistrationClient{}

	mgr := newTestManager(regClient, cfgClient, store, runtimeState, "token")

	err := mgr.FetchConfiguration(context.Background())
	if err != nil {
		t.Fatalf("FetchConfiguration: %v", err)
	}

	// All intervals should remain at their defaults.
	if runtimeState.ServiceStatusInterval() != 1*time.Hour {
		t.Errorf("expected ServiceStatusInterval=1h (default), got %v", runtimeState.ServiceStatusInterval())
	}
	if runtimeState.PingInterval() != 60*time.Second {
		t.Errorf("expected PingInterval=60s (default), got %v", runtimeState.PingInterval())
	}
}

