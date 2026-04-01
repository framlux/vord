// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package sender dequeues telemetry from the local SQLite queue and sends it to the server via gRPC.
package sender

import (
	"context"
	"fmt"
	"log/slog"
	"math"
	"runtime/debug"
	"sync"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
)

// FastTypes are instability/security signals sent every 15s.
var FastTypes = []db.TelemetryType{
	db.TelemetryCpuUsage,
	db.TelemetryMemoryUsage,
	db.TelemetryDiskUsage,
	db.TelemetrySSHSession,
	db.TelemetryServiceStatus,
}

// SlowTypes are static/slow-changing data sent every 5min.
var SlowTypes = []db.TelemetryType{
	db.TelemetrySystemInfo,
	db.TelemetryOsVersion,
	db.TelemetryCpuInfo,
	db.TelemetryMemoryInfo,
	db.TelemetryDiskInfo,
	db.TelemetryHardwareHealth,
	db.TelemetryPackageUpdates,
}

const (
	maxBatchSize   = 50
	maxBackoff     = 60 * time.Second
	initialBackoff = 1 * time.Second
)

// tierStream holds a per-tier gRPC stream and its mutex.
type tierStream struct {
	mu     sync.Mutex
	stream pb.Telemetry_StreamTelemetryClient
}

// Sender dequeues telemetry from the local SQLite queue and sends it to the server via gRPC.
type Sender struct {
	store   *db.Store
	client  pb.TelemetryClient
	rs      *state.RuntimeState
	logger  *slog.Logger

	streams map[string]*tierStream
}

// New creates a new Sender.
func New(store *db.Store, client pb.TelemetryClient, rs *state.RuntimeState) *Sender {
	return &Sender{
		store:  store,
		client: client,
		rs:     rs,
		logger: slog.Default().With("component", "sender"),
		streams: map[string]*tierStream{
			"fast": {},
			"slow": {},
		},
	}
}

// Run starts the two-tier send loops. Blocks until ctx is cancelled.
func (s *Sender) Run(ctx context.Context) {
	// Reset any items stuck in processing state from a previous crash.
	if err := s.store.ResetProcessingTelemetry(); err != nil {
		s.logger.Warn("failed to reset processing telemetry", "error", err)
	}

	var wg sync.WaitGroup
	wg.Add(2)

	go func() {
		defer wg.Done()
		defer func() {
			if r := recover(); r != nil {
				s.logger.Error("sender tier goroutine panicked",
					"tier", "fast",
					"panic", r,
					"stack", string(debug.Stack()),
				)
			}
		}()
		s.runTier(ctx, "fast", FastTypes, s.rs.TelemetrySendFastInterval, s.rs.TelemetrySendFastInterval())
	}()

	go func() {
		defer wg.Done()
		defer func() {
			if r := recover(); r != nil {
				s.logger.Error("sender tier goroutine panicked",
					"tier", "slow",
					"panic", r,
					"stack", string(debug.Stack()),
				)
			}
		}()
		s.runTier(ctx, "slow", SlowTypes, s.rs.TelemetrySendSlowInterval, s.rs.TelemetrySendSlowInterval())
	}()

	wg.Wait()
}

func (s *Sender) runTier(ctx context.Context, tier string, types []db.TelemetryType, getInterval func() time.Duration, initialInterval time.Duration) {
	interval := initialInterval

	// Do an initial send immediately.
	s.sendBatch(ctx, tier, types)

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			// Close the existing stream before flushing so that sendBatch
			// creates a fresh stream bound to the new flush context instead of
			// reusing the cached stream whose parent context is already cancelled.
			ts := s.streams[tier]
			ts.mu.Lock()
			if ts.stream != nil {
				ts.stream.CloseSend()
				ts.stream = nil
			}
			ts.mu.Unlock()

			// Attempt a final flush with a short-lived context before exiting.
			s.logger.Info("flushing remaining telemetry on shutdown", "tier", tier)
			flushCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			s.sendBatch(flushCtx, tier, types)
			cancel()

			// Close any stream created during flush.
			ts.mu.Lock()
			if ts.stream != nil {
				ts.stream.CloseSend()
				ts.stream = nil
			}
			ts.mu.Unlock()

			return
		case <-ticker.C:
			s.sendBatch(ctx, tier, types)

			if newInterval := getInterval(); newInterval != interval {
				s.logger.Info("send interval changed", "tier", tier, "old", interval, "new", newInterval)
				interval = newInterval
				ticker.Reset(interval)
			}
		}
	}
}

func (s *Sender) sendBatch(ctx context.Context, tier string, types []db.TelemetryType) {
	items, err := s.store.DequeueTelemetryByTypes(types, maxBatchSize)
	if err != nil {
		s.logger.Warn("failed to dequeue telemetry", "tier", tier, "error", err)
		return
	}
	if len(items) == 0 {
		return
	}

	envelope := s.buildEnvelope(items)
	ids := make([]string, len(items))
	for i, item := range items {
		ids[i] = item.ID
	}

	s.logger.Debug("sending telemetry batch", "tier", tier, "count", len(items), "batch_id", envelope.BatchId)

	// Try streaming first, fall back to unary.
	ack, err := s.sendViaStream(ctx, tier, envelope)
	if err != nil {
		s.logger.Debug("stream send failed, falling back to unary", "error", err)
		ack, err = s.sendViaUnary(ctx, envelope)
	}

	if err != nil {
		s.logger.Warn("failed to send telemetry batch", "tier", tier, "error", err)
		if markErr := s.store.MarkTelemetryPending(ids); markErr != nil {
			s.logger.Error("failed to mark telemetry back to pending", "error", markErr)
		}
		return
	}

	if ack.Success {
		if err := s.store.MarkTelemetryCompleted(ids); err != nil {
			s.logger.Error("failed to mark telemetry completed", "error", err)
		}
		s.logger.Debug("telemetry batch acknowledged", "tier", tier, "count", len(ids))
	} else {
		s.logger.Warn("server rejected telemetry batch", "tier", tier, "error", ack.ErrorMessage)
		if markErr := s.store.MarkTelemetryPending(ids); markErr != nil {
			s.logger.Error("failed to mark telemetry back to pending", "error", markErr)
		}
	}
}

func (s *Sender) buildEnvelope(items []db.TelemetryQueueItem) *pb.TelemetryEnvelope {
	envelope := &pb.TelemetryEnvelope{
		BatchId:        id.NewV7(),
		AgentTimestamp: timestamppb.Now(),
	}

	for _, item := range items {
		telItem := &pb.TelemetryItem{
			EventId:     item.ID,
			Type:        pb.TelemetryTypes(item.ItemType),
			CollectedAt: parseTimestamp(item.CreatedAt),
		}

		s.setPayload(telItem, item)
		envelope.Items = append(envelope.Items, telItem)
	}

	return envelope
}

// payloadEntry describes how to unmarshal a telemetry type's JSON payload and wrap it in a TelemetryItem oneof.
type payloadEntry struct {
	newMsg  func() proto.Message
	wrapMsg func(proto.Message) *pb.TelemetryItem
}

// payloadDispatch maps telemetry types to their proto message factory and TelemetryItem wrapper.
var payloadDispatch = map[db.TelemetryType]payloadEntry{
	db.TelemetrySystemInfo: {
		newMsg: func() proto.Message { return &pb.SystemInfoRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_SystemInfo{SystemInfo: m.(*pb.SystemInfoRecord)}}
		},
	},
	db.TelemetryOsVersion: {
		newMsg: func() proto.Message { return &pb.OsVersionRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_OsVersion{OsVersion: m.(*pb.OsVersionRecord)}}
		},
	},
	db.TelemetryCpuInfo: {
		newMsg: func() proto.Message { return &pb.CpuInfoRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_CpuInfo{CpuInfo: m.(*pb.CpuInfoRecord)}}
		},
	},
	db.TelemetryMemoryInfo: {
		newMsg: func() proto.Message { return &pb.MemoryInfoRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_MemoryInfo{MemoryInfo: m.(*pb.MemoryInfoRecord)}}
		},
	},
	db.TelemetryDiskInfo: {
		newMsg: func() proto.Message { return &pb.DiskInfoRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_DiskInfo{DiskInfo: m.(*pb.DiskInfoRecord)}}
		},
	},
	db.TelemetryCpuUsage: {
		newMsg: func() proto.Message { return &pb.CpuUtilizationRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_CpuUtilization{CpuUtilization: m.(*pb.CpuUtilizationRecord)}}
		},
	},
	db.TelemetryMemoryUsage: {
		newMsg: func() proto.Message { return &pb.MemoryUtilizationRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_MemoryUtilization{MemoryUtilization: m.(*pb.MemoryUtilizationRecord)}}
		},
	},
	db.TelemetryDiskUsage: {
		newMsg: func() proto.Message { return &pb.DiskUtilizationRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_DiskUtilization{DiskUtilization: m.(*pb.DiskUtilizationRecord)}}
		},
	},
	db.TelemetrySSHSession: {
		newMsg: func() proto.Message { return &pb.SshSessionRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_SshSession{SshSession: m.(*pb.SshSessionRecord)}}
		},
	},
	db.TelemetryHardwareHealth: {
		newMsg: func() proto.Message { return &pb.HardwareHealthRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_HardwareHealth{HardwareHealth: m.(*pb.HardwareHealthRecord)}}
		},
	},
	db.TelemetryPackageUpdates: {
		newMsg: func() proto.Message { return &pb.PackageUpdatesRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_PackageUpdates{PackageUpdates: m.(*pb.PackageUpdatesRecord)}}
		},
	},
	db.TelemetryServiceStatus: {
		newMsg: func() proto.Message { return &pb.ServiceStatusRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_ServiceStatus{ServiceStatus: m.(*pb.ServiceStatusRecord)}}
		},
	},
}

func (s *Sender) setPayload(telItem *pb.TelemetryItem, item db.TelemetryQueueItem) {
	entry, ok := payloadDispatch[item.ItemType]
	if ok == false {
		s.logger.Debug("unknown telemetry type, skipping payload", "type", item.ItemType, "event_id", item.ID)

		return
	}

	msg := entry.newMsg()
	if err := jsonToProto(item.Payload, msg); err != nil {
		s.logger.Debug("failed to unmarshal telemetry payload", "type", item.ItemType, "event_id", item.ID, "error", err)

		return
	}

	wrapped := entry.wrapMsg(msg)
	telItem.Payload = wrapped.Payload
}

func (s *Sender) sendViaStream(ctx context.Context, tier string, envelope *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
	ts := s.streams[tier]
	ts.mu.Lock()
	defer ts.mu.Unlock()

	if ts.stream == nil {
		var err error
		ts.stream, err = s.client.StreamTelemetry(ctx)
		if err != nil {
			return nil, fmt.Errorf("opening telemetry stream: %w", err)
		}
	}

	if err := ts.stream.Send(envelope); err != nil {
		ts.stream.CloseSend()
		ts.stream = nil

		return nil, fmt.Errorf("sending on stream: %w", err)
	}

	ack, err := ts.stream.Recv()
	if err != nil {
		ts.stream.CloseSend()
		ts.stream = nil

		return nil, fmt.Errorf("receiving ack from stream: %w", err)
	}

	return ack, nil
}

func (s *Sender) sendViaUnary(ctx context.Context, envelope *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
	var lastErr error
	backoff := initialBackoff

	for attempt := 0; attempt < 3; attempt++ {
		ack, err := s.client.SubmitTelemetry(ctx, envelope)
		if err == nil {
			return ack, nil
		}
		lastErr = err

		s.logger.Debug("unary submit failed, retrying", "attempt", attempt+1, "backoff", backoff, "error", err)

		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(backoff):
		}

		backoff = time.Duration(math.Min(float64(backoff)*2, float64(maxBackoff)))
	}

	return nil, fmt.Errorf("unary submit failed after 3 attempts: %w", lastErr)
}

func parseTimestamp(rfc3339 string) *timestamppb.Timestamp {
	t, err := time.Parse(time.RFC3339, rfc3339)
	if err != nil {
		return timestamppb.Now()
	}
	return timestamppb.New(t)
}

// jsonToProto unmarshals a JSON payload into a protobuf message.
// The JSON field names from the collectors use snake_case which matches the proto JSON names.
func jsonToProto(payload string, msg proto.Message) error {
	return protojson.Unmarshal([]byte(payload), msg)
}