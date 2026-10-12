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
	"github.com/framlux/vord/internal/jitter"
	"github.com/framlux/vord/internal/kubelocal"
	"github.com/framlux/vord/internal/kubereporter"
	"github.com/framlux/vord/internal/pause"
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

	// Shared jitter source so background loops and the registration backoff do not fire on
	// identical schedules across a fleet of agents (thundering herd).
	jit := jitter.NewDefault()

	// Registration flow with exponential backoff retry.
	regManager := registration.NewManager(grpcClient.Registration, grpcClient.Configuration, store, runtimeState, cfg.RegistrationToken)
	if err := registerWithRetry(ctx, regManager, jit); err != nil {
		slog.Error("registration failed after all retries", "error", err)
		os.Exit(1)
	}

	// Fetch initial configuration.
	if err := regManager.FetchConfiguration(ctx); err != nil {
		slog.Warn("failed to fetch initial configuration, using defaults", "error", err)
	}

	// The pause coordinator is built before the sender and the scheduler that gate on it. Its hooks reach the
	// SSH collector and the sender, which need the coordinator themselves, so both are declared here and
	// assigned below. Every goroutine that can trigger a transition is started after that assignment.
	var sshCollector *collector.SSHSessionsCollector
	var telemetrySender *sender.Sender
	pauseCoordinator := pause.New(time.Now, runtimeState.PingInterval, newPauseHooks(ctx, store,
		func() { telemetrySender.CloseStreams() },
		func(c context.Context, s *db.Store) error { return sshCollector.ResetToNow(c, s) },
	))

	// Initialize command processor if remote commands are enabled. The cluster reporter commands report a state
	// change through a hook that runs the local Kubernetes collector, which the scheduler also runs, so the
	// collector is shared.
	kubeCollector := collector.NewKubernetesCollector(cfg.DataDir)

	var cmdProcessor *commands.Processor
	if cfg.AllowRemoteCommands {
		kubeReporter := kubereporter.New(kubereporter.Options{
			DataDir:  cfg.DataDir,
			Facts:    kubelocal.Detector{Root: "/"}.Detect,
			Releaser: kubereporter.NoopLeaseReleaser{},
			OnStateChanged: newReporterStateHook(
				func(c context.Context) error { return kubeCollector.Collect(c, store) },
				pauseCoordinator.Paused,
			),
		})
		cmdHandler := commands.NewHandler(commands.HandlerDeps{KubeReporter: kubeReporter})
		nonceAdapter := &storeNonceAdapter{store: store}
		ackAdapter := &grpcAckAdapter{client: grpcClient.Configuration}
		cmdProcessor = commands.NewProcessor(cmdHandler, nonceAdapter, ackAdapter, destructiveCommandDelay)
	}

	// Register independent collectors. Grouped collectors (CpuUsage, MemUsage,
	// DiskUsage, MemoryInfo, DiskInfo, SystemInfo, OsVersion, CpuInfo) are
	// handled by FastTick and SlowTick inside the scheduler.
	registry := collector.NewRegistry()
	sshCollector = collector.NewSSHSessionsCollector(runtimeState)
	registry.Register(sshCollector)
	registry.Register(collector.NewHwHealthCollector())
	registry.Register(collector.NewPackagesCollector())
	registry.Register(collector.NewAgentVersionCollector(version))
	registry.Register(kubeCollector)
	registry.RegisterDynamic(collector.NewServicesCollector(runtimeState), runtimeState.ServiceStatusInterval)

	telemetrySender = sender.New(store, grpcClient.Telemetry, runtimeState, jit, pauseCoordinator)

	var wg sync.WaitGroup

	// Start scheduler in background.
	scheduler := collector.NewScheduler(registry, store, runtimeState, pauseCoordinator)
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
		runHeartbeat(ctx, regManager, runtimeState, jit, pauseCoordinator)
	}()

	// Start config refresh loop in background.
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer recoverPanic("config-refresh")
		runConfigRefresh(ctx, regManager, runtimeState, jit)
	}()

	// Start command poll loop in background (opt-in only).
	if cfg.AllowRemoteCommands {
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer recoverPanic("command-poll")
			runCommandPoll(ctx, grpcClient.Configuration, cmdProcessor, runtimeState, jit, pauseCoordinator)
		}()
	} else {
		slog.Info("remote command polling disabled by configuration")
	}

	// Start telemetry sender in background.
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

// newPauseHooks builds what the pause coordinator does on each transition. The sender and the SSH collector are
// passed as functions because they are constructed after the coordinator (both take it), so the caller binds them
// late.
func newPauseHooks(ctx context.Context, store *db.Store, closeStreams func(), resetSSH func(ctx context.Context, store *db.Store) error) pause.Hooks {
	return pause.Hooks{
		OnPause: func(_ pause.Reason) {
			// Nothing queued before the pause should be sent after it.
			if err := store.ClearTelemetryQueue(); err != nil {
				slog.Error("failed to clear telemetry queue on pause", "error", err)
			}

			// Closing waits for any send still in flight on a stream, and a stuck send must not hold up the
			// heartbeat or the other send tier, which both run this hook through the coordinator.
			go closeStreams()
		},
		OnResume: func() {
			// A collector tick that passed the pause gate just before the pause began can still enqueue
			// after the clear in OnPause. Collectors are gated for the whole pause, so nothing legitimate
			// is queued by now and whatever is there is such a straggler, which must not be sent.
			if err := store.ClearTelemetryQueue(); err != nil {
				slog.Error("failed to clear telemetry queue on resume", "error", err)
			}

			// Skip the SSH events logged during the pause instead of collecting and alerting on them.
			if err := resetSSH(ctx, store); err != nil {
				slog.Error("failed to reset ssh collection point on resume", "error", err)
			}

			// The first CPU sample after the pause becomes the baseline rather than an average over it.
			if err := store.SaveCollectorState("cpu_usage", nil); err != nil {
				slog.Error("failed to reset cpu baseline on resume", "error", err)
			}
		},
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
func registerWithRetry(ctx context.Context, regManager *registration.Manager, jit *jitter.Jitter) error {
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
			case <-time.After(jit.Apply(backoff, 0.2)):
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

func runHeartbeat(ctx context.Context, reg *registration.Manager, runtimeState *state.RuntimeState, jit *jitter.Jitter, pauseCoordinator *pause.Coordinator) {
	interval := runtimeState.PingInterval()
	// The first fire is offset by a random startup phase; subsequent fires are jittered around the
	// interval so a fleet of agents does not all ping at the same instant.
	timer := time.NewTimer(jit.StartupPhase(interval))
	defer timer.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
			pingState, err := reg.Ping(ctx)
			if err != nil {
				slog.Warn("heartbeat ping failed", "error", err)
			} else {
				slog.Debug("heartbeat ping sent")
			}

			// A ping that carried a state is authoritative whether or not the heartbeat write succeeded, and
			// a healthy ping with no state means the host is not paused. A ping that failed with no state says
			// nothing, so the lease is neither renewed nor ended by it.
			if (err == nil) || (pingState != pb.IngestRejectionReason_INGEST_REJECTION_REASON_UNSPECIFIED) {
				pauseCoordinator.Observe(pause.FromProto(pingState))
			}

			if newInterval := runtimeState.PingInterval(); newInterval != interval {
				slog.Info("heartbeat interval changed", "old", interval.String(), "new", newInterval.String())
				interval = newInterval
			}
			timer.Reset(jit.Apply(interval, 0.15))
		}
	}
}

func runConfigRefresh(ctx context.Context, reg *registration.Manager, runtimeState *state.RuntimeState, jit *jitter.Jitter) {
	interval := runtimeState.ConfigRefreshInterval()
	timer := time.NewTimer(jit.StartupPhase(interval))
	defer timer.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
			if err := reg.FetchConfiguration(ctx); err != nil {
				slog.Warn("config refresh failed", "error", err)
			} else {
				slog.Debug("configuration refreshed")
			}

			if newInterval := runtimeState.ConfigRefreshInterval(); newInterval != interval {
				slog.Info("config refresh interval changed", "old", interval.String(), "new", newInterval.String())
				interval = newInterval
			}
			timer.Reset(jit.Apply(interval, 0.15))
		}
	}
}

func runCommandPoll(ctx context.Context, cfgClient pb.ConfigurationClient, processor *commands.Processor, rs *state.RuntimeState, jit *jitter.Jitter, pauseCoordinator *pause.Coordinator) {
	interval := rs.CommandPollInterval()
	timer := time.NewTimer(jit.StartupPhase(interval))
	defer timer.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
			// A paused host's commands are not delivered, and one created before the pause is bounded by its own
			// expiry, so there is nothing to poll for until the pause ends.
			if pauseCoordinator.Paused() {
				slog.Debug("command poll skipped, host is paused")
			} else {
				pollCommands(ctx, cfgClient, processor, rs)
			}

			if newInterval := rs.CommandPollInterval(); newInterval != interval {
				interval = newInterval
			}
			timer.Reset(jit.Apply(interval, 0.15))
		}
	}
}

// pollCommands fetches the commands waiting for this machine and hands each to the processor.
func pollCommands(ctx context.Context, cfgClient pb.ConfigurationClient, processor *commands.Processor, rs *state.RuntimeState) {
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
