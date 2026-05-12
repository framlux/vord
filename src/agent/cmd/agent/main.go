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
	store := db.NewStore(database, cfg.MaxQueueSize)
	defer store.Close()

	// Initialize state.
	runtimeState := state.New()

	// Build agent capabilities bitmask from configuration.
	var capabilities uint64
	if cfg.AllowRemoteCommands {
		capabilities |= state.CapabilityRemoteCommands
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

	// Initialize command processor if remote commands are enabled.
	var cmdProcessor *commands.Processor
	if cfg.AllowRemoteCommands {
		cmdHandler := commands.NewHandler()
		nonceAdapter := &storeNonceAdapter{store: store}
		ackAdapter := &grpcAckAdapter{client: grpcClient.Configuration}
		cmdProcessor = commands.NewProcessor(cmdHandler, nonceAdapter, ackAdapter, destructiveCommandDelay)
	}

	// Register independent collectors. Grouped collectors (CpuUsage, MemUsage,
	// DiskUsage, MemoryInfo, DiskInfo, SystemInfo, OsVersion, CpuInfo) are
	// handled by FastTick and SlowTick inside the scheduler.
	registry := collector.NewRegistry()
	registry.Register(collector.NewSSHSessionsCollector(runtimeState))
	registry.Register(collector.NewHwHealthCollector())
	registry.Register(collector.NewPackagesCollector())
	registry.RegisterDynamic(collector.NewServicesCollector(runtimeState), runtimeState.ServiceStatusInterval)

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
			runCommandPoll(ctx, grpcClient.Configuration, cmdProcessor, runtimeState)
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

func runCommandPoll(ctx context.Context, cfgClient pb.ConfigurationClient, processor *commands.Processor, rs *state.RuntimeState) {
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
					processor.Process(ctx, commands.PendingCommand{
						ID:               cmd.Id,
						Type:             cmd.Type,
						Params:           cmd.Params,
						CanonicalPayload: cmd.CanonicalPayload,
						Signature:        cmd.Signature,
						SigningKeyID:     cmd.SigningKeyId,
						Timestamp:        cmd.Timestamp,
						ExpiresAt:        cmd.ExpiresAt,
						Nonce:            cmd.Nonce,
						UserID:           cmd.UserId,
						TenantID:         cmd.TenantId,
						MachineID:        cmd.MachineId,
					}, rs.TenantID(), rs.MachineID())
				}
			}

			if newInterval := rs.CommandPollInterval(); newInterval != interval {
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

// storeNonceAdapter adapts *db.Store to the commands.NonceStore interface.
type storeNonceAdapter struct {
	store *db.Store
}

func (a *storeNonceAdapter) IsNonceUsed(nonce string) (bool, error) {
	return a.store.IsNonceUsed(nonce)
}

func (a *storeNonceAdapter) RecordNonce(nonce string) error {
	return a.store.RecordNonce(nonce)
}

func (a *storeNonceAdapter) GetSigningKey(keyID int32) (*commands.SigningKey, error) {
	k, err := a.store.GetSigningKey(keyID)
	if err != nil {
		return nil, err
	}

	return &commands.SigningKey{
		KeyID:     k.KeyID,
		UserID:    k.UserID,
		PublicKey: k.PublicKey,
	}, nil
}

// grpcAckAdapter adapts the gRPC ConfigurationClient to the commands.CommandAcknowledger interface.
type grpcAckAdapter struct {
	client pb.ConfigurationClient
}

func (a *grpcAckAdapter) Acknowledge(ctx context.Context, commandID string, machineID int64, success bool, exitCode int32, stdout string, stderr string, message string, resultType int32) {
	_, err := a.client.AcknowledgeCommand(ctx, &pb.AcknowledgeCommandRequest{
		CommandId: commandID,
		MachineId: machineID,
		Result: &pb.CommandResult{
			Success:    success,
			ExitCode:   exitCode,
			Stdout:     stdout,
			Stderr:     stderr,
			Message:    message,
			ResultType: pb.ResultType(resultType),
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