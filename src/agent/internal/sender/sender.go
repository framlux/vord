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
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/id"
	"github.com/framlux/vord/internal/jitter"
	"github.com/framlux/vord/internal/pause"
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
	db.TelemetryAgentVersion,
	db.TelemetryKubernetesNode,
}

const (
	maxBatchSize   = 50
	maxBackoff     = 60 * time.Second
	initialBackoff = 1 * time.Second

	// maxEnvelopeBytes is the most item data one envelope carries. The server refuses any message over 1 MB, and
	// the margin below that covers the envelope's own fields.
	maxEnvelopeBytes = 768 * 1024
	// maxItemBytes is the largest single item the sender puts on the wire. Anything bigger could never be
	// accepted and would block its tier forever if left queued, so it is discarded instead.
	maxItemBytes = 900 * 1024
	// itemFramingBytes bounds the field tag and length prefix added when an item is placed in an envelope,
	// which proto.Size of the item alone does not count.
	itemFramingBytes = 8
)

// tierStream holds a per-tier gRPC stream and its mutex. The mutex is held for the whole of a send, from writing
// the envelope to reading its ack, so it is only ever held by sendViaStream and the close paths, and none of them
// calls out to anything that could want another lock while holding it.
type tierStream struct {
	mu     sync.Mutex
	stream pb.Telemetry_StreamTelemetryClient
}

// Sender dequeues telemetry from the local SQLite queue and sends it to the server via gRPC.
type Sender struct {
	store  *db.Store
	client pb.TelemetryClient
	rs     *state.RuntimeState
	logger *slog.Logger
	jit    *jitter.Jitter
	// pause says whether the server has paused this host. A paused sender sends nothing, and an ack that
	// reports a pause puts it there.
	pause *pause.Coordinator
	// clock supplies the current time for stamping the envelope's AgentTimestamp. Defaults to
	// time.Now in production; tests inject a fixed closure for deterministic timestamp assertions.
	clock func() time.Time
	// lastAck is when the server last acknowledged an envelope with success, nil before the first. It is read by
	// other goroutines, such as the cluster reporter's health gate, so it is an atomic pointer.
	lastAck atomic.Pointer[time.Time]

	streams map[string]*tierStream
}

// New creates a new Sender.
func New(store *db.Store, client pb.TelemetryClient, rs *state.RuntimeState, jit *jitter.Jitter, pc *pause.Coordinator) *Sender {
	return &Sender{
		store:  store,
		client: client,
		rs:     rs,
		logger: slog.Default().With("component", "sender"),
		jit:    jit,
		pause:  pc,
		clock:  time.Now,
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

	// The first send is offset by a random startup phase (bounded by the interval) and subsequent
	// sends are jittered around the interval so a fleet of agents does not all send at the same
	// instant. The shutdown branch below still flushes any queued telemetry.
	timer := time.NewTimer(s.jit.StartupPhase(interval))
	defer timer.Stop()

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

			// A paused host has nothing to flush: the queue was cleared when it paused and the server would
			// discard anything sent.
			if s.pause.Paused() {
				s.logger.Info("skipping shutdown flush, host is paused", "tier", tier)

				return
			}

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
		case <-timer.C:
			s.sendBatch(ctx, tier, types)

			if newInterval := getInterval(); newInterval != interval {
				s.logger.Info("send interval changed", "tier", tier, "old", interval.String(), "new", newInterval.String())
				interval = newInterval
			}
			timer.Reset(s.jit.Apply(interval, 0.15))
		}
	}
}

// LastAckAt returns when the server last acknowledged an envelope with success, or the zero time when it has not
// yet. Only an acknowledgement that reports success counts: a rejected batch, a batch that never reached the
// server and an acknowledgement that says the host is paused or its subscription inactive do not.
func (s *Sender) LastAckAt() time.Time {
	acked := s.lastAck.Load()
	if acked == nil {
		return time.Time{}
	}

	return *acked
}

// CloseStreams closes every tier's cached stream so the next send opens a fresh one. A pause calls it to stop
// holding a stream open to a server that is discarding what it receives.
//
// Lock order: each tier lock is taken alone, one after another, and nothing else is held or taken while it is.
// A send in flight holds its tier lock until its ack is read, so this waits for that send to finish; callers
// that must not wait (the heartbeat) run it on its own goroutine.
func (s *Sender) CloseStreams() {
	for _, ts := range s.streams {
		ts.mu.Lock()
		if ts.stream != nil {
			ts.stream.CloseSend()
			ts.stream = nil
		}
		ts.mu.Unlock()
	}
}

func (s *Sender) sendBatch(ctx context.Context, tier string, types []db.TelemetryType) {
	if s.pause.Paused() {
		return
	}

	items, err := s.store.DequeueTelemetryByTypes(types, maxBatchSize)
	if err != nil {
		s.logger.Warn("failed to dequeue telemetry", "tier", tier, "error", err)
		return
	}
	if len(items) == 0 {
		return
	}

	chunks, oversized := splitByBudget(s.buildItems(items), maxEnvelopeBytes, maxItemBytes)
	if len(oversized) > 0 {
		for _, item := range oversized {
			s.logger.Error("discarding telemetry item too large for the server to accept",
				"tier", tier, "type", item.Type, "event_id", item.EventId, "bytes", proto.Size(item), "limit", maxItemBytes)
		}
		if _, err := s.store.DiscardTelemetry(eventIDs(oversized)); err != nil {
			s.logger.Error("failed to discard oversized telemetry", "tier", tier, "error", err)
		}
	}

	for i, chunk := range chunks {
		// A pause can begin while an earlier envelope is in flight, for instance when a heartbeat ping reports
		// it. By then the queue has been cleared and the streams closed, so sending on would put pre-pause data
		// on the wire and reopen a stream that nothing would close until the pause ends.
		if i > 0 && s.pause.Paused() {
			s.markUnsentPending(chunks[i:])

			return
		}

		if s.sendEnvelope(ctx, tier, chunk) {
			continue
		}

		// Stop at the first envelope that was not delivered, or that left the host paused.
		s.markUnsentPending(chunks[i+1:])

		return
	}
}

// markUnsentPending puts the items of envelopes that were never sent back to pending so the next tick retries
// them in order. If a pause cleared the queue, these ids are already gone and marking them changes nothing.
func (s *Sender) markUnsentPending(unsentChunks [][]*pb.TelemetryItem) {
	var unsent []string
	for _, chunk := range unsentChunks {
		unsent = append(unsent, eventIDs(chunk)...)
	}
	if markErr := s.store.MarkTelemetryPending(unsent); markErr != nil {
		s.logger.Error("failed to mark telemetry back to pending", "error", markErr)
	}
}

// sendEnvelope sends one envelope and settles its items in the queue: completed when the server accepts them,
// pending when it does not. It reports whether the caller may go on to send the next envelope, which it may not
// after a failure or once the server has reported the host paused.
func (s *Sender) sendEnvelope(ctx context.Context, tier string, items []*pb.TelemetryItem) bool {
	envelope := s.newEnvelope(items)
	ids := eventIDs(items)

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

		return false
	}

	// An ack that carries a pause reason reports success so the batch is dropped, but it says the server is
	// discarding this host's data. It must not count as the server taking it, or a paused host would look healthy
	// to whatever reads LastAckAt.
	pauseReason := pause.FromProto(ack.Reason)

	delivered := ack.Success
	if delivered {
		if pauseReason == pause.None {
			acked := s.clock()
			s.lastAck.Store(&acked)
		}

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

	// A paused host's batch is acknowledged as a success and discarded, so it is already marked completed
	// above; what matters here is that the host must now stop sending. This runs after sendViaStream has
	// released the tier lock, because entering the pause runs hooks that close the streams and take it again.
	// An ack only ever starts or renews a pause: a reason this agent does not know is ignored rather than
	// read as "resumed", and a plain ack proves nothing about a pause that is already in force.
	if pauseReason != pause.None {
		s.pause.Observe(pauseReason)

		return false
	}

	return delivered
}

// buildItems converts queued rows into wire items, keeping their order.
func (s *Sender) buildItems(items []db.TelemetryQueueItem) []*pb.TelemetryItem {
	telItems := make([]*pb.TelemetryItem, 0, len(items))
	for _, item := range items {
		telItem := &pb.TelemetryItem{
			EventId:     item.ID,
			Type:        pb.TelemetryTypes(item.ItemType),
			CollectedAt: parseTimestamp(item.CreatedAt),
		}

		s.setPayload(telItem, item)
		telItems = append(telItems, telItem)
	}

	return telItems
}

// newEnvelope wraps wire items in a freshly identified, timestamped envelope.
func (s *Sender) newEnvelope(items []*pb.TelemetryItem) *pb.TelemetryEnvelope {
	return &pb.TelemetryEnvelope{
		BatchId:        id.NewV7(),
		AgentTimestamp: timestamppb.New(s.clock()),
		Items:          items,
	}
}

// buildEnvelope converts queued rows into a single envelope.
func (s *Sender) buildEnvelope(items []db.TelemetryQueueItem) *pb.TelemetryEnvelope {
	return s.newEnvelope(s.buildItems(items))
}

// splitByBudget groups items, in order, into envelopes whose summed encoded size stays within budget. An item
// larger than the budget but within maxItem goes in an envelope of its own. An item larger than maxItem can
// never be accepted by the server, so it is returned as oversized and placed in no envelope.
func splitByBudget(items []*pb.TelemetryItem, budget, maxItem int) (chunks [][]*pb.TelemetryItem, oversized []*pb.TelemetryItem) {
	var current []*pb.TelemetryItem
	currentSize := 0
	for _, item := range items {
		size := proto.Size(item) + itemFramingBytes
		if size > maxItem {
			oversized = append(oversized, item)

			continue
		}
		if len(current) > 0 && currentSize+size > budget {
			chunks = append(chunks, current)
			current = nil
			currentSize = 0
		}
		current = append(current, item)
		currentSize += size
	}
	if len(current) > 0 {
		chunks = append(chunks, current)
	}

	return chunks, oversized
}

func eventIDs(items []*pb.TelemetryItem) []string {
	ids := make([]string, len(items))
	for i, item := range items {
		ids[i] = item.EventId
	}

	return ids
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
	db.TelemetryAgentVersion: {
		newMsg: func() proto.Message { return &pb.AgentVersionRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_AgentVersion{AgentVersion: m.(*pb.AgentVersionRecord)}}
		},
	},
	db.TelemetryKubernetesNode: {
		newMsg: func() proto.Message { return &pb.KubernetesNodeRecord{} },
		wrapMsg: func(m proto.Message) *pb.TelemetryItem {
			return &pb.TelemetryItem{Payload: &pb.TelemetryItem_KubernetesNode{KubernetesNode: m.(*pb.KubernetesNodeRecord)}}
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
