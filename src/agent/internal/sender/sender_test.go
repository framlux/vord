// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package sender

import (
	"context"
	"fmt"
	"testing"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/metadata"

	"github.com/framlux/vord/internal/db"
	pb "github.com/framlux/vord/internal/proto/agent"
)

// --- Mock implementations ---

// mockTelemetryClient implements pb.TelemetryClient with configurable behavior.
type mockTelemetryClient struct {
	submitFunc func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error)
	streamFunc func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error)

	submitCalls int
	streamCalls int
}

func (m *mockTelemetryClient) SubmitTelemetry(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
	m.submitCalls++
	if m.submitFunc != nil {
		return m.submitFunc(ctx, in, opts...)
	}

	return &pb.TelemetryAck{Success: true}, nil
}

func (m *mockTelemetryClient) StreamTelemetry(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
	m.streamCalls++
	if m.streamFunc != nil {
		return m.streamFunc(ctx, opts...)
	}

	return nil, fmt.Errorf("stream not available")
}

// mockBidiStream implements grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck].
type mockBidiStream struct {
	grpc.ClientStream
	sendFunc func(*pb.TelemetryEnvelope) error
	recvFunc func() (*pb.TelemetryAck, error)
}

func (m *mockBidiStream) Send(env *pb.TelemetryEnvelope) error {
	if m.sendFunc != nil {
		return m.sendFunc(env)
	}

	return nil
}

func (m *mockBidiStream) Recv() (*pb.TelemetryAck, error) {
	if m.recvFunc != nil {
		return m.recvFunc()
	}

	return &pb.TelemetryAck{Success: true}, nil
}

func (m *mockBidiStream) CloseSend() error {
	return nil
}

func (m *mockBidiStream) Header() (metadata.MD, error) {
	return nil, nil
}

func (m *mockBidiStream) Trailer() metadata.MD {
	return nil
}

func (m *mockBidiStream) Context() context.Context {
	return context.Background()
}

func (m *mockBidiStream) SendMsg(_ any) error {
	return nil
}

func (m *mockBidiStream) RecvMsg(_ any) error {
	return nil
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

// --- parseTimestamp tests ---

// Intent: RFC3339 timestamps parse correctly.
func TestParseTimestamp_ValidFormats(t *testing.T) {
	ts := parseTimestamp("2026-01-15T10:30:00Z")

	got := ts.AsTime()
	want := time.Date(2026, 1, 15, 10, 30, 0, 0, time.UTC)
	if got.Equal(want) == false {
		t.Errorf("expected %v, got %v", want, got)
	}
}

// Intent: Invalid timestamp strings return current time (not panic).
func TestParseTimestamp_Invalid(t *testing.T) {
	before := time.Now().Add(-1 * time.Second)
	ts := parseTimestamp("not-a-timestamp")
	after := time.Now().Add(1 * time.Second)

	got := ts.AsTime()
	if got.Before(before) || got.After(after) {
		t.Errorf("expected timestamp near now for invalid input, got %v", got)
	}
}

// Intent: Empty string returns current time (not panic).
func TestParseTimestamp_Empty(t *testing.T) {
	before := time.Now().Add(-1 * time.Second)
	ts := parseTimestamp("")
	after := time.Now().Add(1 * time.Second)

	got := ts.AsTime()
	if got.Before(before) || got.After(after) {
		t.Errorf("expected timestamp near now for empty input, got %v", got)
	}
}

// --- jsonToProto tests ---

// Intent: Valid JSON converts to protobuf message correctly.
func TestJsonToProto_ValidStruct(t *testing.T) {
	msg := &pb.CpuUtilizationRecord{}
	err := jsonToProto(`{"cpuUsagePercent": 42}`, msg)
	if err != nil {
		t.Fatalf("jsonToProto: %v", err)
	}

	if msg.CpuUsagePercent != 42 {
		t.Errorf("expected CpuUsagePercent=42, got %d", msg.CpuUsagePercent)
	}
}

// Intent: Invalid JSON returns error (not panic).
func TestJsonToProto_InvalidJSON(t *testing.T) {
	msg := &pb.CpuUtilizationRecord{}
	err := jsonToProto("not valid json{{{", msg)
	if err == nil {
		t.Error("expected error for invalid JSON, got nil")
	}
}

// --- payloadDispatch tests ---

// Intent: All 12 db.Telemetry* constants have entries in payloadDispatch map.
func TestPayloadDispatch_AllTypesRegistered(t *testing.T) {
	allTypes := []db.TelemetryType{
		db.TelemetrySystemInfo,
		db.TelemetryOsVersion,
		db.TelemetryCpuInfo,
		db.TelemetryMemoryInfo,
		db.TelemetryDiskInfo,
		db.TelemetryCpuUsage,
		db.TelemetryMemoryUsage,
		db.TelemetryDiskUsage,
		db.TelemetrySSHSession,
		db.TelemetryHardwareHealth,
		db.TelemetryPackageUpdates,
		db.TelemetryServiceStatus,
	}

	for _, tt := range allTypes {
		if _, ok := payloadDispatch[tt]; ok == false {
			t.Errorf("payloadDispatch missing entry for TelemetryType %d", tt)
		}
	}

	if len(payloadDispatch) != len(allTypes) {
		t.Errorf("payloadDispatch has %d entries, expected %d", len(payloadDispatch), len(allTypes))
	}
}

// Intent: Cross-reference db telemetry type constants against dispatch map keys — no extra or missing types.
func TestPayloadDispatch_NoMissingTypes(t *testing.T) {
	// Verify FastTypes + SlowTypes cover all types.
	allKnown := make(map[db.TelemetryType]bool)
	for _, tt := range FastTypes {
		allKnown[tt] = true
	}
	for _, tt := range SlowTypes {
		allKnown[tt] = true
	}

	for key := range payloadDispatch {
		if allKnown[key] == false {
			t.Errorf("payloadDispatch key %d not in FastTypes or SlowTypes", key)
		}
	}
}

// --- setPayload tests ---

// Intent: Unknown telemetry type skips payload silently (no panic, no crash).
func TestSetPayload_UnknownType(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-1",
		ItemType: db.TelemetryType(999), // unknown type
		Payload:  `{"key":"value"}`,
	}

	// Should not panic.
	s.setPayload(telItem, item)

	// Payload should remain nil.
	if telItem.Payload != nil {
		t.Errorf("expected nil payload for unknown type, got %v", telItem.Payload)
	}
}

// Intent: Malformed JSON payload does not panic, payload remains nil.
func TestSetPayload_MalformedJSON(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-2",
		ItemType: db.TelemetryCpuUsage,
		Payload:  "not valid json{{{",
	}

	// Should not panic.
	s.setPayload(telItem, item)

	// Payload should remain nil because JSON was invalid.
	if telItem.Payload != nil {
		t.Errorf("expected nil payload for malformed JSON, got %v", telItem.Payload)
	}
}

// Intent: CPU usage JSON correctly maps to proto CpuUtilization payload.
func TestSetPayload_CpuUsage(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-cpu",
		ItemType: db.TelemetryCpuUsage,
		Payload:  `{"cpuUsagePercent": 75, "userTime": 40, "systemTime": 20}`,
	}

	s.setPayload(telItem, item)

	cpuPayload := telItem.GetCpuUtilization()
	if cpuPayload == nil {
		t.Fatal("expected CpuUtilization payload, got nil")
	}
	if cpuPayload.CpuUsagePercent != 75 {
		t.Errorf("expected CpuUsagePercent=75, got %d", cpuPayload.CpuUsagePercent)
	}
}

// Intent: Memory usage JSON correctly maps to proto MemoryUtilization payload.
func TestSetPayload_MemoryUsage(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-mem",
		ItemType: db.TelemetryMemoryUsage,
		Payload:  `{"memoryTotal": 16000000000, "memoryUsed": 8000000000, "memoryUsagePercent": 50}`,
	}

	s.setPayload(telItem, item)

	memPayload := telItem.GetMemoryUtilization()
	if memPayload == nil {
		t.Fatal("expected MemoryUtilization payload, got nil")
	}
}

// Intent: System info JSON correctly maps to proto SystemInfo payload.
func TestSetPayload_SystemInfo(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-sys",
		ItemType: db.TelemetrySystemInfo,
		Payload:  `{"hostname": "testhost", "uptimeSeconds": "3600"}`,
	}

	s.setPayload(telItem, item)

	sysPayload := telItem.GetSystemInfo()
	if sysPayload == nil {
		t.Fatal("expected SystemInfo payload, got nil")
	}
	if sysPayload.Hostname != "testhost" {
		t.Errorf("expected Hostname=testhost, got %q", sysPayload.Hostname)
	}
}

// --- buildEnvelope tests ---

// Intent: buildEnvelope creates correct protobuf envelope with matching fields for each telemetry type.
func TestBuildEnvelope_AllTelemetryTypes(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	items := []db.TelemetryQueueItem{
		{
			ID:        "item-1",
			ItemType:  db.TelemetryCpuUsage,
			Payload:   `{"cpuUsagePercent": 50}`,
			CreatedAt: "2026-01-15T10:30:00Z",
		},
		{
			ID:        "item-2",
			ItemType:  db.TelemetryMemoryUsage,
			Payload:   `{"memoryUsagePercent": 60}`,
			CreatedAt: "2026-01-15T10:30:01Z",
		},
	}

	envelope := s.buildEnvelope(items)

	if envelope.BatchId == "" {
		t.Error("expected non-empty BatchId")
	}
	if envelope.AgentTimestamp == nil {
		t.Error("expected non-nil AgentTimestamp")
	}
	if len(envelope.Items) != 2 {
		t.Fatalf("expected 2 items, got %d", len(envelope.Items))
	}

	if envelope.Items[0].EventId != "item-1" {
		t.Errorf("expected EventId=item-1, got %q", envelope.Items[0].EventId)
	}
	if envelope.Items[1].EventId != "item-2" {
		t.Errorf("expected EventId=item-2, got %q", envelope.Items[1].EventId)
	}
}

// Intent: buildEnvelope with invalid type still builds envelope (unknown type just has no payload).
func TestBuildEnvelope_InvalidType(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	items := []db.TelemetryQueueItem{
		{
			ID:        "item-bad",
			ItemType:  db.TelemetryType(999),
			Payload:   `{"key": "value"}`,
			CreatedAt: "2026-01-15T10:30:00Z",
		},
	}

	envelope := s.buildEnvelope(items)

	if len(envelope.Items) != 1 {
		t.Fatalf("expected 1 item, got %d", len(envelope.Items))
	}
	if envelope.Items[0].Payload != nil {
		t.Errorf("expected nil payload for unknown type, got %v", envelope.Items[0].Payload)
	}
}

// --- New tests ---

// Intent: Constructor sets correct default streams map.
func TestNewSender_Defaults(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	if s.store != store {
		t.Error("expected store to be set")
	}
	if s.client != client {
		t.Error("expected client to be set")
	}
	if len(s.streams) != 2 {
		t.Errorf("expected 2 stream tiers, got %d", len(s.streams))
	}
	if _, ok := s.streams["fast"]; ok == false {
		t.Error("missing 'fast' stream tier")
	}
	if _, ok := s.streams["slow"]; ok == false {
		t.Error("missing 'slow' stream tier")
	}
}

// --- sendBatch tests ---

// Intent: No items in queue → no RPC calls, no errors.
func TestSendBatch_EmptyQueue(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if client.submitCalls != 0 {
		t.Errorf("expected 0 submit calls for empty queue, got %d", client.submitCalls)
	}
	if client.streamCalls != 0 {
		t.Errorf("expected 0 stream calls for empty queue, got %d", client.streamCalls)
	}
}

// Intent: Server returns success → items marked completed in DB.
func TestSendBatch_ServerAck(t *testing.T) {
	store := newTestStore(t)

	// Enqueue some telemetry.
	if err := store.EnqueueTelemetry("ack-1", db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	client := &mockTelemetryClient{
		// Stream will fail, falling back to unary which succeeds.
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return nil, fmt.Errorf("stream unavailable")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true}, nil
		},
	}
	s := New(store, client)

	s.sendBatch(context.Background(), "fast", FastTypes)

	// Items should be marked completed — dequeue should return nothing.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(items) != 0 {
		t.Errorf("expected 0 pending items after ack, got %d", len(items))
	}
}

// Intent: Server returns error → items marked back to pending (retry later).
func TestSendBatch_ServerReject(t *testing.T) {
	store := newTestStore(t)

	if err := store.EnqueueTelemetry("rej-1", db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return nil, fmt.Errorf("stream unavailable")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: false, ErrorMessage: "rejected"}, nil
		},
	}
	s := New(store, client)

	s.sendBatch(context.Background(), "fast", FastTypes)

	// Items should be back to pending — dequeue should return them.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 pending item after rejection, got %d", len(items))
	}
}

// Intent: Stream RPC fails → falls back to unary, items still sent successfully.
func TestSendBatch_StreamFallbackToUnary(t *testing.T) {
	store := newTestStore(t)

	if err := store.EnqueueTelemetry("fb-1", db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	streamFailed := false
	unarySucceeded := false

	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			streamFailed = true

			return nil, fmt.Errorf("stream unavailable")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			unarySucceeded = true

			return &pb.TelemetryAck{Success: true}, nil
		},
	}
	s := New(store, client)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if streamFailed == false {
		t.Error("expected stream to be attempted")
	}
	if unarySucceeded == false {
		t.Error("expected unary fallback to be used")
	}
}

// Intent: Unary succeeds on first attempt — no retries.
func TestSendViaUnary_FirstAttemptSuccess(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true}, nil
		},
	}
	s := New(store, client)

	envelope := &pb.TelemetryEnvelope{BatchId: "test-batch"}
	ack, err := s.sendViaUnary(context.Background(), envelope)
	if err != nil {
		t.Fatalf("sendViaUnary: %v", err)
	}
	if ack.Success == false {
		t.Error("expected Success=true")
	}
	if client.submitCalls != 1 {
		t.Errorf("expected 1 submit call, got %d", client.submitCalls)
	}
}

// Intent: Context cancellation stops retry loop.
func TestSendViaUnary_ContextCancelled(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return nil, fmt.Errorf("transient error")
		},
	}
	s := New(store, client)

	ctx, cancel := context.WithCancel(context.Background())
	cancel() // Cancel immediately.

	envelope := &pb.TelemetryEnvelope{BatchId: "test-batch"}
	_, err := s.sendViaUnary(ctx, envelope)
	if err == nil {
		t.Error("expected error for cancelled context")
	}
}

// --- sendViaStream tests ---

// Intent: Stream send and receive works correctly.
func TestSendViaStream_Success(t *testing.T) {
	store := newTestStore(t)

	stream := &mockBidiStream{
		sendFunc: func(env *pb.TelemetryEnvelope) error {
			return nil
		},
		recvFunc: func() (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true, BatchId: "batch-1"}, nil
		},
	}

	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return stream, nil
		},
	}
	s := New(store, client)

	envelope := &pb.TelemetryEnvelope{BatchId: "batch-1"}
	ack, err := s.sendViaStream(context.Background(), "fast", envelope)
	if err != nil {
		t.Fatalf("sendViaStream: %v", err)
	}
	if ack.Success == false {
		t.Error("expected Success=true")
	}
}

// Intent: Stream send failure resets the stream and returns error.
func TestSendViaStream_SendFailure(t *testing.T) {
	store := newTestStore(t)

	stream := &mockBidiStream{
		sendFunc: func(env *pb.TelemetryEnvelope) error {
			return fmt.Errorf("send failed")
		},
	}

	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return stream, nil
		},
	}
	s := New(store, client)

	envelope := &pb.TelemetryEnvelope{BatchId: "batch-1"}
	_, err := s.sendViaStream(context.Background(), "fast", envelope)
	if err == nil {
		t.Error("expected error for stream send failure")
	}

	// Stream should be reset to nil.
	ts := s.streams["fast"]
	ts.mu.Lock()
	defer ts.mu.Unlock()
	if ts.stream != nil {
		t.Error("expected stream to be reset to nil after failure")
	}
}

// --- FastTypes/SlowTypes tests ---

// Intent: Fast tier processes only fast telemetry types.
func TestRunTier_FastTypes(t *testing.T) {
	expectedFast := map[db.TelemetryType]bool{
		db.TelemetryCpuUsage:      true,
		db.TelemetryMemoryUsage:   true,
		db.TelemetryDiskUsage:     true,
		db.TelemetrySSHSession:    true,
		db.TelemetryServiceStatus: true,
	}

	for _, tt := range FastTypes {
		if expectedFast[tt] == false {
			t.Errorf("unexpected type %d in FastTypes", tt)
		}
	}

	if len(FastTypes) != len(expectedFast) {
		t.Errorf("expected %d fast types, got %d", len(expectedFast), len(FastTypes))
	}
}

// Intent: Slow tier processes only slow telemetry types.
func TestRunTier_SlowTypes(t *testing.T) {
	expectedSlow := map[db.TelemetryType]bool{
		db.TelemetrySystemInfo:     true,
		db.TelemetryOsVersion:      true,
		db.TelemetryCpuInfo:        true,
		db.TelemetryMemoryInfo:     true,
		db.TelemetryDiskInfo:       true,
		db.TelemetryHardwareHealth: true,
		db.TelemetryPackageUpdates: true,
	}

	for _, tt := range SlowTypes {
		if expectedSlow[tt] == false {
			t.Errorf("unexpected type %d in SlowTypes", tt)
		}
	}

	if len(SlowTypes) != len(expectedSlow) {
		t.Errorf("expected %d slow types, got %d", len(expectedSlow), len(SlowTypes))
	}
}

// --- Batch size tests ---

// Intent: sendBatch respects configured batch size limit.
func TestSendBatch_RespectsMaxBatchSize(t *testing.T) {
	store := newTestStore(t)

	// Enqueue more items than maxBatchSize.
	for i := 0; i < 60; i++ {
		id := fmt.Sprintf("batch-%d", i)
		if err := store.EnqueueTelemetry(id, db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
			t.Fatalf("enqueue %d: %v", i, err)
		}
	}

	var receivedCount int
	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return nil, fmt.Errorf("stream unavailable")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			receivedCount = len(in.Items)

			return &pb.TelemetryAck{Success: true}, nil
		},
	}
	s := New(store, client)

	s.sendBatch(context.Background(), "fast", FastTypes)

	// maxBatchSize is 50, so only 50 should be sent.
	if receivedCount > 50 {
		t.Errorf("expected at most 50 items in batch, got %d", receivedCount)
	}
}

// Intent: All RPC failures → items marked back to pending for retry.
func TestSendBatch_AllRPCsFail_ItemsReturnToPending(t *testing.T) {
	store := newTestStore(t)

	if err := store.EnqueueTelemetry("fail-1", db.TelemetryCpuUsage, `{}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return nil, fmt.Errorf("stream failed")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return nil, fmt.Errorf("unary failed")
		},
	}
	s := New(store, client)

	// Use a short-lived context to avoid waiting for retry backoffs.
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()

	s.sendBatch(ctx, "fast", FastTypes)

	// Items should be back in pending state.
	items, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(items) != 1 {
		t.Errorf("expected 1 item back in pending, got %d", len(items))
	}
}
