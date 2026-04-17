// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"runtime/debug"
	"sync"
	"syscall"
	"time"

	"github.com/framlux/vord/internal/collector"
	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/config"
	"github.com/framlux/vord/internal/crypto"
	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/grpcclient"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/registration"
	"github.com/framlux/vord/internal/sender"
	"github.com/framlux/vord/internal/state"
)

var (
	version   = "dev"
	buildTime = "unknown"
)

const (
	// Registration retry settings.
	maxRegistrationAttempts = 10
	registrationTimeout     = 2 * time.Minute

	// Shutdown timeout for waiting on goroutines to finish.
	shutdownTimeout = 15 * time.Second

	// Delay before executing destructive commands to allow ACK to flush.
	destructiveCommandDelay = 10 * time.Second
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		slog.Error("failed to load configuration", "error", err)
		os.Exit(1)
	}

	// Create the data directory before opening the database.
	if err := cfg.EnsureDataDir(); err != nil {
		slog.Error("failed to create data directory", "error", err)
		os.Exit(1)
	}

	setupLogging(cfg.LogLevel)

	slog.Info("vord agent starting",
		"version", version,
		"build_time", buildTime,
		"server", cfg.GRPCTarget(),
		"data_dir", cfg.DataDir,
	)

	// Open database.
	database, err := db.Open(cfg.DatabasePath())
	if err != nil {
		slog.Error("failed to open database", "error", err)
		os.Exit(1)
	}
	store := db.NewStore(database)
	defer store.Close()

	// Initialize state.
	runtimeState := state.New()

	// Build agent capabilities bitmask from configuration.
	var capabilities uint64
	if cfg.AllowRemoteCommands {
		capabilities |= 1 // bit 0 = remote commands enabled
	}
	runtimeState.SetAgentCapabilities(capabilities)

	// Set up gRPC client.
	grpcClient, err := grpcclient.New(cfg.GRPCTarget(), runtimeState, cfg.UseTLS)
	if err != nil {
		slog.Error("failed to create gRPC client", "error", err)
		os.Exit(1)
	}
	defer grpcClient.Close()

	// Set up context with cancellation on signals.
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		sig := <-sigCh
		slog.Info("received signal, shutting down", "signal", sig)
		cancel()
	}()

	// Registration flow with exponential backoff retry.
	regManager := registration.NewManager(grpcClient.Registration, grpcClient.Configuration, store, runtimeState, cfg.RegistrationToken)
	if err := registerWithRetry(ctx, regManager); err != nil {
		slog.Error("registration failed after all retries", "error", err)
		os.Exit(1)
	}

	// Fetch initial configuration.
	if err := regManager.FetchConfiguration(ctx); err != nil {
		slog.Warn("failed to fetch initial configuration, using defaults", "error", err)
	}

	// Initialize command handler if remote commands are enabled.
	var cmdHandler *commands.Handler
	if cfg.AllowRemoteCommands {
		cmdHandler = commands.NewHandler()
	}

	// Register independent collectors. Grouped collectors (CpuUsage, MemUsage,
	// DiskUsage, MemoryInfo, DiskInfo, SystemInfo, OsVersion, CpuInfo) are
	// handled by FastTick and SlowTick inside the scheduler.
	registry := collector.NewRegistry()
	registry.Register(collector.NewSSHSessionsCollector(runtimeState))
	registry.Register(collector.NewHwHealthCollector())
	registry.Register(collector.NewPackagesCollector())
	registry.Register(collector.NewServicesCollector())

	var wg sync.WaitGroup

	// Start scheduler in background.
	scheduler := collector.NewScheduler(registry, store, runtimeState)
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("scheduler")
		scheduler.Run(ctx)
	}()

	// Start heartbeat loop in background.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("heartbeat")
		runHeartbeat(ctx, regManager, runtimeState)
	}()

	// Start config refresh loop in background.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("config-refresh")
		runConfigRefresh(ctx, regManager, runtimeState)
	}()

	// Start command poll loop in background (opt-in only).
	if cfg.AllowRemoteCommands {
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer recoverPanic("command-poll")
			runCommandPoll(ctx, grpcClient.Configuration, cmdHandler, runtimeState, store)
		}()
	} else {
		slog.Info("remote command polling disabled by configuration")
	}

	// Start telemetry sender in background.
	telemetrySender := sender.New(store, grpcClient.Telemetry, runtimeState)
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("telemetry-sender")
		telemetrySender.Run(ctx)
	}()

	// Start telemetry queue purge loop in background.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("telemetry-purge")
		runPurge(ctx, store)
	}()

	// Block until shutdown signal, then wait for goroutines with a timeout.
	<-ctx.Done()
	slog.Info("shutting down, waiting for goroutines...")

	shutdownDone := make(chan struct{})
	go func() {
		wg.Wait()
		close(shutdownDone)
	}()

	select {
	case <-shutdownDone:
		slog.Info("vord agent stopped gracefully")
	case <-time.After(shutdownTimeout):
		slog.Warn("shutdown timed out, exiting", "timeout", shutdownTimeout)
	}
}

// recoverPanic logs a panic and stack trace, allowing the goroutine to exit cleanly.
func recoverPanic(component string) {
	if r := recover(); r != nil {
		slog.Error("goroutine panicked",
			"component", component,
			"panic", r,
			"stack", string(debug.Stack()),
		)
	}
}

// registerWithRetry attempts registration with exponential backoff.
// Backoff intervals: 5s, 10s, 30s, 60s, 120s (repeating last value).
// Up to 10 attempts before giving up. Each attempt has a 2-minute timeout.
func registerWithRetry(ctx context.Context, regManager *registration.Manager) error {
	backoffs := []time.Duration{
		5 * time.Second,
		10 * time.Second,
		30 * time.Second,
		60 * time.Second,
		120 * time.Second,
	}
	for attempt := 1; attempt <= maxRegistrationAttempts; attempt++ {
		regCtx, regCancel := context.WithTimeout(ctx, registrationTimeout)
		err := regManager.EnsureRegistered(regCtx)
		regCancel()

		if err == nil {
			slog.Info("registration successful", "attempt", attempt)

			return nil
		}

		// If the parent context is cancelled, stop retrying.
		if ctx.Err() != nil {
			return fmt.Errorf("registration cancelled: %w", ctx.Err())
		}

		// Determine backoff duration for this attempt.
		backoffIdx := attempt - 1
		if backoffIdx >= len(backoffs) {
			backoffIdx = len(backoffs) - 1
		}
		backoff := backoffs[backoffIdx]

		if attempt < maxRegistrationAttempts {
			slog.Warn("registration attempt failed, retrying",
				"attempt", attempt,
				"max_attempts", maxRegistrationAttempts,
				"backoff", backoff,
				"error", err,
			)

			select {
			case <-ctx.Done():
				return fmt.Errorf("registration cancelled during backoff: %w", ctx.Err())
			case <-time.After(backoff):
				// Continue to next attempt.
			}
		} else {
			return fmt.Errorf("registration failed after %d attempts: %w", maxRegistrationAttempts, err)
		}
	}

	// This is unreachable: the loop always returns from the else branch on the
	// final attempt or from the success/cancelled paths on earlier attempts.
	// Kept for safety in case the loop logic is refactored later.
	return fmt.Errorf("registration failed after %d attempts", maxRegistrationAttempts)
}

func setupLogging(level string) {
	var logLevel slog.Level
	switch level {
	case "debug":
		logLevel = slog.LevelDebug
	case "warn":
		logLevel = slog.LevelWarn
	case "error":
		logLevel = slog.LevelError
	default:
		logLevel = slog.LevelInfo
	}

	handler := slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{
		Level: logLevel,
	})
	slog.SetDefault(slog.New(handler))
}

func runHeartbeat(ctx context.Context, reg *registration.Manager, runtimeState *state.RuntimeState) {
	interval := runtimeState.PingInterval()
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if err := reg.Ping(ctx); err != nil {
				slog.Warn("heartbeat ping failed", "error", err)
			} else {
				slog.Debug("heartbeat ping sent")
			}

			if newInterval := runtimeState.PingInterval(); newInterval != interval {
				slog.Info("heartbeat interval changed", "old", interval.String(), "new", newInterval.String())
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func runConfigRefresh(ctx context.Context, reg *registration.Manager, runtimeState *state.RuntimeState) {
	interval := runtimeState.ConfigRefreshInterval()
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			if err := reg.FetchConfiguration(ctx); err != nil {
				slog.Warn("config refresh failed", "error", err)
			} else {
				slog.Debug("configuration refreshed")
			}

			if newInterval := runtimeState.ConfigRefreshInterval(); newInterval != interval {
				slog.Info("config refresh interval changed", "old", interval.String(), "new", newInterval.String())
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func runCommandPoll(ctx context.Context, cfgClient pb.ConfigurationClient, handler *commands.Handler, rs *state.RuntimeState, store *db.Store) {
	interval := rs.CommandPollInterval()
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			resp, err := cfgClient.GetPendingCommands(ctx, &pb.GetPendingCommandsRequest{
				MachineId: rs.MachineID(),
			})
			if err != nil {
				slog.Debug("command poll failed", "error", err)
			} else {
				for _, cmd := range resp.Commands {
					processCommand(ctx, cfgClient, handler, rs, store, cmd)
				}
			}

			if newInterval := rs.CommandPollInterval(); newInterval != interval {
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func processCommand(ctx context.Context, cfgClient pb.ConfigurationClient, handler *commands.Handler, rs *state.RuntimeState, store *db.Store, cmd *pb.AgentCommand) {
	cmdType := commands.CommandType(cmd.Type)

	// 0a. Validate the command type before any further processing.
	if cmdType.IsValid() == false {
		slog.Error("unknown command type, rejecting", "command_id", cmd.Id, "type", cmd.Type)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "unknown command type", pb.ResultType_REJECTED)

		return
	}

	// 0b. Verify command ownership: tenant and machine must match agent identity.
	if cmd.TenantId != rs.TenantID() {
		slog.Error("command tenant mismatch, rejecting", "command_id", cmd.Id, "cmd_tenant", cmd.TenantId, "agent_tenant", rs.TenantID())
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "tenant mismatch", pb.ResultType_REJECTED)

		return
	}
	if cmd.MachineId != rs.MachineID() {
		slog.Error("command machine mismatch, rejecting", "command_id", cmd.Id, "cmd_machine", cmd.MachineId, "agent_machine", rs.MachineID())
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "machine mismatch", pb.ResultType_REJECTED)

		return
	}

	// 1. Look up signing key.
	key, err := store.GetSigningKey(cmd.SigningKeyId)
	if err != nil {
		slog.Error("unknown signing key, rejecting command", "command_id", cmd.Id, "key_id", cmd.SigningKeyId)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "unknown signing key", pb.ResultType_REJECTED)

		return
	}

	// 2. Verify Ed25519 signature.
	canonicalPayload := []byte(cmd.CanonicalPayload)
	if crypto.VerifySignature(key.PublicKey, canonicalPayload, cmd.Signature) == false {
		slog.Error("invalid signature, rejecting command", "command_id", cmd.Id)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "invalid signature", pb.ResultType_REJECTED)

		return
	}

	// 3. Validate timestamps.
	if err := crypto.ValidateTimestamps(cmd.Timestamp, cmd.ExpiresAt, 5*time.Minute); err != nil {
		slog.Error("timestamp validation failed, rejecting command", "command_id", cmd.Id, "error", err)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", err.Error(), pb.ResultType_REJECTED)

		return
	}

	// 4. Check nonce.
	used, err := store.IsNonceUsed(cmd.Nonce)
	if err != nil {
		slog.Error("nonce check failed", "command_id", cmd.Id, "error", err)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "nonce check error", pb.ResultType_REJECTED)

		return
	}
	if used {
		slog.Warn("nonce already used, rejecting replay", "command_id", cmd.Id, "nonce", cmd.Nonce)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "nonce already used", pb.ResultType_REJECTED)

		return
	}

	// 5. Record nonce — reject the command if recording fails to prevent replay.
	if err := store.RecordNonce(cmd.Nonce); err != nil {
		slog.Error("failed to record nonce, rejecting command", "command_id", cmd.Id, "error", err)
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, false, -1, "", "", "nonce recording failed", pb.ResultType_REJECTED)

		return
	}

	// 6. Execute command, handling destructive commands with a delay.
	if cmdType.IsDestructive() {
		// ACK with Initiated before executing destructive command.
		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, true, 0, "", "", "will execute in 10s", pb.ResultType_INITIATED)

		// Wait to allow ACK to flush to server, but respect context cancellation.
		select {
		case <-time.After(destructiveCommandDelay):
		case <-ctx.Done():
			return
		}

		result := handler.Execute(ctx, commands.Command{
			Type:   cmdType,
			Params: cmd.Params,
		})
		if result.Error != nil {
			slog.Error("destructive command failed", "command_id", cmd.Id, "type", cmd.Type, "error", result.Error)
		} else {
			slog.Info("destructive command executed", "command_id", cmd.Id, "type", cmd.Type, "message", result.Message)
		}
	} else {
		// Execute immediately and ACK with full result.
		result := handler.Execute(ctx, commands.Command{
			Type:   cmdType,
			Params: cmd.Params,
		})

		acknowledgeCommand(ctx, cfgClient, rs, cmd.Id, result.Success, 0, "", "", result.Message, pb.ResultType_COMPLETED)

		if result.Error != nil {
			slog.Error("command execution failed", "command_id", cmd.Id, "type", cmd.Type, "error", result.Error)
		} else {
			slog.Info("command executed", "command_id", cmd.Id, "type", cmd.Type, "message", result.Message)
		}
	}
}

func acknowledgeCommand(ctx context.Context, cfgClient pb.ConfigurationClient, rs *state.RuntimeState, commandID string, success bool, exitCode int32, stdout string, stderr string, message string, resultType pb.ResultType) {
	_, err := cfgClient.AcknowledgeCommand(ctx, &pb.AcknowledgeCommandRequest{
		CommandId: commandID,
		MachineId: rs.MachineID(),
		Result: &pb.CommandResult{
			Success:    success,
			ExitCode:   exitCode,
			Stdout:     stdout,
			Stderr:     stderr,
			Message:    message,
			ResultType: resultType,
		},
	})
	if err != nil {
		slog.Error("failed to acknowledge command", "command_id", commandID, "error", err)
	}
}

func runPurge(ctx context.Context, store *db.Store) {
	ticker := time.NewTicker(1 * time.Hour)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			purged, err := store.PurgeTelemetry(24 * time.Hour)
			if err != nil {
				slog.Warn("telemetry purge failed", "error", err)
			} else if purged > 0 {
				slog.Info("purged completed telemetry", "count", purged)
			}

			sshPurged, err := store.PurgeSSHSessions(7 * 24 * time.Hour)
			if err != nil {
				slog.Warn("ssh session purge failed", "error", err)
			} else if sshPurged > 0 {
				slog.Info("purged old ssh sessions", "count", sshPurged)
			}

			noncePurged, err := store.PurgeNonces(24 * time.Hour)
			if err != nil {
				slog.Warn("nonce purge failed", "error", err)
			} else if noncePurged > 0 {
				slog.Info("purged old command nonces", "count", noncePurged)
			}
		}
	}
}