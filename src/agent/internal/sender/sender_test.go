// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package sender

import (
	"context"
	"fmt"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/metadata"
	"google.golang.org/protobuf/proto"

	"github.com/framlux/vord/internal/db"
	"github.com/framlux/vord/internal/jitter"
	"github.com/framlux/vord/internal/pause"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
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

	closeCalls int
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
	m.closeCalls++

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

	return db.NewStore(database, 0)
}

// neverPaused returns a coordinator that is never in the paused state, for tests that do not exercise pausing.
func neverPaused() *pause.Coordinator {
	return pause.New(time.Now, func() time.Duration { return time.Minute }, pause.Hooks{})
}

// pauseRecorder builds a coordinator with a lease long enough that it cannot lapse during a test, and counts
// the transitions into pause.
type pauseRecorder struct {
	coord   *pause.Coordinator
	pauses  atomic.Int32
	reasons []pause.Reason
}

func newPauseRecorder(onPause func(pause.Reason)) *pauseRecorder {
	r := &pauseRecorder{}
	r.coord = pause.New(time.Now, func() time.Duration { return time.Hour }, pause.Hooks{
		OnPause: func(reason pause.Reason) {
			r.reasons = append(r.reasons, reason)
			r.pauses.Add(1)
			if onPause != nil {
				onPause(reason)
			}
		},
	})

	return r
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

// Intent: All db.Telemetry* constants have entries in payloadDispatch map.
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
		db.TelemetryAgentVersion,
		db.TelemetryKubernetesNode,
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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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

// Intent: Agent version JSON correctly maps to the proto AgentVersion payload so the server
// receives the version on the slow tier instead of an empty oneof.
func TestSetPayload_AgentVersion(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-agent-version",
		ItemType: db.TelemetryAgentVersion,
		Payload:  `{"version": "1.16.0"}`,
	}

	s.setPayload(telItem, item)

	versionPayload := telItem.GetAgentVersion()
	if versionPayload == nil {
		t.Fatal("expected AgentVersion payload, got nil")
	}
	if versionPayload.Version != "1.16.0" {
		t.Errorf("expected Version=1.16.0, got %q", versionPayload.Version)
	}
}

// Intent: Kubernetes node JSON correctly maps to the proto KubernetesNode payload so the server receives
// the node identity on the slow tier instead of an empty oneof.
func TestSetPayload_KubernetesNode(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	telItem := &pb.TelemetryItem{}
	item := db.TelemetryQueueItem{
		ID:       "test-kubernetes-node",
		ItemType: db.TelemetryKubernetesNode,
		Payload: `{"distro": "k3s", "kubeletVersion": "v1.31.4+k3s1", "nodeName": "cp-1", "nodeNameSource": "certificate", ` +
			`"hasKubelet": true, "controlPlane": true, "caFingerprint": "ab", "adminCredentialPresent": true, "reporterEnrolled": true}`,
	}

	s.setPayload(telItem, item)

	node := telItem.GetKubernetesNode()
	if node == nil {
		t.Fatal("expected KubernetesNode payload, got nil")
	}
	if node.Distro != "k3s" || node.KubeletVersion != "v1.31.4+k3s1" || node.NodeName != "cp-1" || node.NodeNameSource != "certificate" {
		t.Errorf("unexpected string fields: %+v", node)
	}
	if node.HasKubelet == false || node.ControlPlane == false || node.AdminCredentialPresent == false || node.ReporterEnrolled == false {
		t.Errorf("expected every bool field true, got %+v", node)
	}
	if node.CaFingerprint != "ab" {
		t.Errorf("expected CaFingerprint=ab, got %q", node.CaFingerprint)
	}
}

// Intent: the reason a reporter cannot use its Lease credentials reaches the server in the node report, under the
// snake_case key the local collector writes.
func TestSetPayload_KubernetesNode_CarriesReporterError(t *testing.T) {
	store := newTestStore(t)
	s := New(store, &mockTelemetryClient{}, state.New(), jitter.NewDefault(), neverPaused())

	telItem := &pb.TelemetryItem{}
	s.setPayload(telItem, db.TelemetryQueueItem{
		ID:       "test-kubernetes-node-error",
		ItemType: db.TelemetryKubernetesNode,
		Payload:  `{"distro": "k3s", "node_name": "cp-1", "reporter_enrolled": true, "reporter_error": "credentials_rejected"}`,
	})

	node := telItem.GetKubernetesNode()
	if node == nil {
		t.Fatal("expected KubernetesNode payload, got nil")
	}

	if node.ReporterError != "credentials_rejected" {
		t.Errorf("ReporterError = %q, want credentials_rejected", node.ReporterError)
	}
}

// Intent: System info JSON correctly maps to proto SystemInfo payload.
func TestSetPayload_SystemInfo(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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

// Intent: buildEnvelope stamps AgentTimestamp from the injected clock, deterministically.
func TestBuildEnvelope_AgentTimestampFromInjectedClock(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	fixed := time.Date(2026, 6, 27, 8, 15, 30, 0, time.UTC)
	s.clock = func() time.Time { return fixed }

	items := []db.TelemetryQueueItem{
		{
			ID:        "item-clock",
			ItemType:  db.TelemetryCpuUsage,
			Payload:   `{"cpuUsagePercent": 10}`,
			CreatedAt: "2026-06-27T08:15:30Z",
		},
	}

	envelope := s.buildEnvelope(items)

	if envelope.AgentTimestamp == nil {
		t.Fatal("expected non-nil AgentTimestamp")
	}
	got := envelope.AgentTimestamp.AsTime()
	if got.Equal(fixed) == false {
		t.Errorf("expected AgentTimestamp %v, got %v", fixed, got)
	}
}

// Intent: buildEnvelope with invalid type still builds envelope (unknown type just has no payload).
func TestBuildEnvelope_InvalidType(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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

// Intent: the cluster reporter's health gate counts from the last time the server accepted a batch, so the
// timestamp must come from the sender's clock at the moment an ack reports success, and from nowhere else.
func TestLastAckAt_SetOnSuccessfulAck(t *testing.T) {
	store := newTestStore(t)
	if err := store.EnqueueTelemetry("ack-time", db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
		t.Fatalf("enqueue: %v", err)
	}

	client := &mockTelemetryClient{
		submitFunc: func(context.Context, *pb.TelemetryEnvelope, ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true}, nil
		},
	}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	fixed := time.Date(2026, 10, 11, 12, 30, 0, 0, time.UTC)
	s.clock = func() time.Time { return fixed }

	if got := s.LastAckAt(); got.IsZero() == false {
		t.Fatalf("LastAckAt before any ack = %v, want the zero time", got)
	}

	s.sendBatch(context.Background(), "fast", FastTypes)

	if got := s.LastAckAt(); got.Equal(fixed) == false {
		t.Errorf("LastAckAt = %v, want %v", got, fixed)
	}
}

// Intent: a batch the server rejects, or one that never reached it, is not an acknowledgement, so it must not make
// a broken delivery path look healthy to the reporter's health gate.
func TestLastAckAt_NotSetOnRejectionOrFailure(t *testing.T) {
	cases := map[string]func(context.Context, *pb.TelemetryEnvelope, ...grpc.CallOption) (*pb.TelemetryAck, error){
		"rejected": func(context.Context, *pb.TelemetryEnvelope, ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: false, ErrorMessage: "rejected"}, nil
		},
		"unreachable": func(context.Context, *pb.TelemetryEnvelope, ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return nil, fmt.Errorf("server unreachable")
		},
	}

	for name, submit := range cases {
		t.Run(name, func(t *testing.T) {
			store := newTestStore(t)
			if err := store.EnqueueTelemetry("no-ack", db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
				t.Fatalf("enqueue: %v", err)
			}

			s := New(store, &mockTelemetryClient{submitFunc: submit}, state.New(), jitter.NewDefault(), neverPaused())
			s.clock = func() time.Time { return time.Date(2026, 10, 11, 12, 30, 0, 0, time.UTC) }

			// A cancelled context stops the unary retries from backing off, so the failure case ends at once.
			ctx, cancel := context.WithCancel(context.Background())
			cancel()

			s.sendBatch(ctx, "fast", FastTypes)

			if got := s.LastAckAt(); got.IsZero() == false {
				t.Errorf("LastAckAt = %v after a %s batch, want the zero time", got, name)
			}
		})
	}
}

// Intent: a later acknowledgement replaces an earlier one, so the gate measures from the newest.
func TestLastAckAt_AdvancesWithEachSuccessfulAck(t *testing.T) {
	store := newTestStore(t)
	client := &mockTelemetryClient{}
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	now := time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC)
	s.clock = func() time.Time { return now }

	for i, offset := range []time.Duration{0, 7 * time.Minute} {
		now = time.Date(2026, 10, 11, 12, 0, 0, 0, time.UTC).Add(offset)

		if err := store.EnqueueTelemetry(fmt.Sprintf("ack-%d", i), db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
			t.Fatalf("enqueue: %v", err)
		}

		s.sendBatch(context.Background(), "fast", FastTypes)

		if got := s.LastAckAt(); got.Equal(now) == false {
			t.Errorf("after ack %d LastAckAt = %v, want %v", i, got, now)
		}
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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
		db.TelemetryAgentVersion:   true,
		db.TelemetryKubernetesNode: true,
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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

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

// --- Pause tests ---

func enqueueCpu(t *testing.T, store *db.Store, id string) {
	t.Helper()
	if err := store.EnqueueTelemetry(id, db.TelemetryCpuUsage, `{"cpuUsagePercent": 50}`); err != nil {
		t.Fatalf("enqueue %s: %v", id, err)
	}
}

func unaryOnlyClient(submit func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error)) *mockTelemetryClient {
	return &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return nil, fmt.Errorf("stream unavailable")
		},
		submitFunc: func(ctx context.Context, in *pb.TelemetryEnvelope, opts ...grpc.CallOption) (*pb.TelemetryAck, error) {
			return submit(in)
		},
	}
}

// Intent: a paused sender takes nothing off the queue and makes no RPC.
func TestSendBatch_WhilePaused_DequeuesNothingAndMakesNoRPC(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "paused-1")
	client := &mockTelemetryClient{}
	rec := newPauseRecorder(nil)
	rec.coord.Observe(pause.HostPaused)
	s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if client.submitCalls != 0 || client.streamCalls != 0 {
		t.Errorf("expected no RPC while paused, got submit=%d stream=%d", client.submitCalls, client.streamCalls)
	}
	if n, err := store.CountPendingTelemetry(); err != nil || n != 1 {
		t.Errorf("expected the item to stay pending, got %d (err %v)", n, err)
	}
}

// Intent: an ack that says the host is paused puts the sender into the pause, while the batch it acknowledges
// is still treated as delivered.
func TestSendBatch_HostPausedAck_EntersPause(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "ack-paused-1")
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		return &pb.TelemetryAck{Success: true, Reason: pb.IngestRejectionReason_INGEST_REJECTION_REASON_HOST_PAUSED}, nil
	})
	rec := newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if rec.coord.Paused() == false {
		t.Fatal("expected the sender to be paused after a HOST_PAUSED ack")
	}
	if rec.pauses.Load() != 1 || rec.reasons[0] != pause.HostPaused {
		t.Errorf("expected one OnPause with HostPaused, got %d calls %v", rec.pauses.Load(), rec.reasons)
	}
	var status int
	if err := store.DB().QueryRow("SELECT status FROM telemetry_queue WHERE id = ?", "ack-paused-1").Scan(&status); err != nil {
		t.Fatalf("query: %v", err)
	}
	if db.QueueStatus(status) != db.QueueStatusCompleted {
		t.Errorf("expected the acknowledged item to be marked completed, got status %d", status)
	}
}

// Intent: an inactive subscription reported on a rejected batch also pauses the sender, and the rejected batch
// is not left half-handled.
func TestSendBatch_SubscriptionInactiveAck_EntersPause(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "ack-inactive-1")
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		return &pb.TelemetryAck{Success: false, ErrorMessage: "inactive", Reason: pb.IngestRejectionReason_INGEST_REJECTION_REASON_SUBSCRIPTION_INACTIVE}, nil
	})
	rec := newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if rec.coord.Paused() == false {
		t.Fatal("expected the sender to be paused after a SUBSCRIPTION_INACTIVE ack")
	}
	if rec.reasons[0] != pause.SubscriptionInactive {
		t.Errorf("expected OnPause with SubscriptionInactive, got %v", rec.reasons)
	}
}

// Intent: an ordinary ack leaves the sender running.
func TestSendBatch_PlainAck_DoesNotPause(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "plain-1")
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		return &pb.TelemetryAck{Success: true}, nil
	})
	rec := newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if rec.coord.Paused() {
		t.Error("expected a plain ack not to pause the sender")
	}
	if rec.pauses.Load() != 0 {
		t.Errorf("expected no OnPause, got %d", rec.pauses.Load())
	}
}

// Intent: a reason from a newer server that this agent does not know is ignored; an ack never reads as a pause
// or a resume it cannot interpret.
func TestSendBatch_UnknownAckReason_IsIgnored(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "unknown-1")
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		return &pb.TelemetryAck{Success: true, Reason: pb.IngestRejectionReason(99)}, nil
	})
	rec := newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	s.sendBatch(context.Background(), "fast", FastTypes)

	if rec.coord.Paused() {
		t.Error("expected an unknown reason not to pause the sender")
	}
	if rec.pauses.Load() != 0 {
		t.Errorf("expected no OnPause, got %d", rec.pauses.Load())
	}
}

// Intent: the pause hook may close the sender's streams from inside the ack handler, which runs on the sender's
// own goroutine, without deadlocking on the tier lock the send just released.
func TestSendBatch_PausedAckOnStream_OnPauseCanCloseStreams(t *testing.T) {
	store := newTestStore(t)
	enqueueCpu(t, store, "stream-paused-1")
	stream := &mockBidiStream{
		recvFunc: func() (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true, Reason: pb.IngestRejectionReason_INGEST_REJECTION_REASON_HOST_PAUSED}, nil
		},
	}
	client := &mockTelemetryClient{
		streamFunc: func(ctx context.Context, opts ...grpc.CallOption) (grpc.BidiStreamingClient[pb.TelemetryEnvelope, pb.TelemetryAck], error) {
			return stream, nil
		},
	}
	var s *Sender
	rec := newPauseRecorder(func(pause.Reason) { s.CloseStreams() })
	s = New(store, client, state.New(), jitter.NewDefault(), rec.coord)

	done := make(chan struct{})
	go func() {
		defer close(done)
		s.sendBatch(context.Background(), "fast", FastTypes)
	}()

	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("sendBatch deadlocked: the pause hook could not close the streams")
	}

	if stream.closeCalls != 1 {
		t.Errorf("expected the cached stream to be closed once, got %d", stream.closeCalls)
	}
	if s.streams["fast"].stream != nil {
		t.Error("expected the fast stream to be dropped so the next send opens a fresh one")
	}
}

// Intent: closing the streams closes every tier's cached stream and forgets it.
func TestCloseStreams_ClosesEveryTier(t *testing.T) {
	store := newTestStore(t)
	s := New(store, &mockTelemetryClient{}, state.New(), jitter.NewDefault(), neverPaused())
	fast := &mockBidiStream{}
	slow := &mockBidiStream{}
	s.streams["fast"].stream = fast
	s.streams["slow"].stream = slow

	s.CloseStreams()

	if fast.closeCalls != 1 || slow.closeCalls != 1 {
		t.Errorf("expected each stream closed once, got fast=%d slow=%d", fast.closeCalls, slow.closeCalls)
	}
	if s.streams["fast"].stream != nil || s.streams["slow"].stream != nil {
		t.Error("expected both cached streams to be dropped")
	}
}

// Intent: closing when no stream is open is harmless.
func TestCloseStreams_NoStreams(t *testing.T) {
	store := newTestStore(t)
	s := New(store, &mockTelemetryClient{}, state.New(), jitter.NewDefault(), neverPaused())

	s.CloseStreams()
}

// Intent: on shutdown a running host still flushes what is queued, but a paused host sends nothing.
func TestRunTier_ShutdownFlush_SkippedWhilePaused(t *testing.T) {
	flushedItems := func(t *testing.T, paused bool) int {
		t.Helper()
		store := newTestStore(t)
		enqueueCpu(t, store, "flush-1")
		client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
			return &pb.TelemetryAck{Success: true}, nil
		})
		rec := newPauseRecorder(nil)
		if paused {
			rec.coord.Observe(pause.HostPaused)
		}
		s := New(store, client, state.New(), jitter.NewDefault(), rec.coord)

		// The tier timer is an hour out, so the already-cancelled context is the only branch that can fire.
		ctx, cancel := context.WithCancel(context.Background())
		cancel()
		s.runTier(ctx, "fast", FastTypes, func() time.Duration { return time.Hour }, time.Hour)

		return client.submitCalls
	}

	if got := flushedItems(t, false); got != 1 {
		t.Errorf("expected the shutdown flush to send the queued item when running, got %d RPC(s)", got)
	}
	if got := flushedItems(t, true); got != 0 {
		t.Errorf("expected no shutdown flush while paused, got %d RPC(s)", got)
	}
}

// --- byte budget tests ---

// osVersionItem builds a telemetry item whose encoded size is roughly size bytes, carried in a free-text field.
func osVersionItem(id string, size int) *pb.TelemetryItem {
	return &pb.TelemetryItem{
		EventId: id,
		Type:    pb.TelemetryTypes(db.TelemetryOsVersion),
		Payload: &pb.TelemetryItem_OsVersion{OsVersion: &pb.OsVersionRecord{Extra: strings.Repeat("x", size)}},
	}
}

// Intent: small items that fit the budget together travel in a single envelope.
func TestSplitByBudget_SmallItemsShareOneEnvelope(t *testing.T) {
	items := []*pb.TelemetryItem{osVersionItem("a", 100), osVersionItem("b", 100), osVersionItem("c", 100)}

	chunks, oversized := splitByBudget(items, 10_000, 20_000)

	if len(oversized) != 0 {
		t.Fatalf("expected nothing oversized, got %d", len(oversized))
	}
	if len(chunks) != 1 || len(chunks[0]) != 3 {
		t.Fatalf("expected one envelope of 3 items, got %v", chunks)
	}
}

// Intent: items are split at the budget, and their order is kept across envelopes, so the server sees them in
// the order they were collected.
func TestSplitByBudget_SplitsAtBudgetAndKeepsOrder(t *testing.T) {
	items := []*pb.TelemetryItem{
		osVersionItem("1", 4_000), osVersionItem("2", 4_000), osVersionItem("3", 4_000), osVersionItem("4", 4_000),
	}

	chunks, oversized := splitByBudget(items, 10_000, 20_000)

	if len(oversized) != 0 {
		t.Fatalf("expected nothing oversized, got %d", len(oversized))
	}
	want := [][]string{{"1", "2"}, {"3", "4"}}
	if len(chunks) != len(want) {
		t.Fatalf("expected %d envelopes, got %d", len(want), len(chunks))
	}
	for i := range want {
		got := eventIDs(chunks[i])
		if strings.Join(got, ",") != strings.Join(want[i], ",") {
			t.Errorf("envelope %d: expected %v, got %v", i, want[i], got)
		}
		size := 0
		for _, item := range chunks[i] {
			size += proto.Size(item) + itemFramingBytes
		}
		if size > 10_000 {
			t.Errorf("envelope %d is %d bytes, over the 10000 budget", i, size)
		}
	}
}

// Intent: an item bigger than the budget but under the item limit is still sendable. It goes alone, and it
// neither drags its neighbours over the budget nor is discarded.
func TestSplitByBudget_ItemOverBudgetGoesAlone(t *testing.T) {
	items := []*pb.TelemetryItem{osVersionItem("small-1", 100), osVersionItem("big", 15_000), osVersionItem("small-2", 100)}

	chunks, oversized := splitByBudget(items, 10_000, 20_000)

	if len(oversized) != 0 {
		t.Fatalf("expected nothing oversized, got %d", len(oversized))
	}
	want := [][]string{{"small-1"}, {"big"}, {"small-2"}}
	if len(chunks) != len(want) {
		t.Fatalf("expected %d envelopes, got %d: %v", len(want), len(chunks), chunks)
	}
	for i := range want {
		if strings.Join(eventIDs(chunks[i]), ",") != strings.Join(want[i], ",") {
			t.Errorf("envelope %d: expected %v, got %v", i, want[i], eventIDs(chunks[i]))
		}
	}
}

// Intent: an item over the item limit can never be accepted, so it is reported as oversized and left out of
// every envelope, while the items around it are still sent.
func TestSplitByBudget_ItemOverLimitIsSetAside(t *testing.T) {
	items := []*pb.TelemetryItem{osVersionItem("ok-1", 100), osVersionItem("huge", 25_000), osVersionItem("ok-2", 100)}

	chunks, oversized := splitByBudget(items, 10_000, 20_000)

	if len(oversized) != 1 || oversized[0].EventId != "huge" {
		t.Fatalf("expected only %q oversized, got %v", "huge", oversized)
	}
	if len(chunks) != 1 || strings.Join(eventIDs(chunks[0]), ",") != "ok-1,ok-2" {
		t.Fatalf("expected one envelope [ok-1 ok-2], got %v", chunks)
	}
}

// Intent: nothing in means nothing out, with no empty envelope that would make a pointless RPC.
func TestSplitByBudget_Empty(t *testing.T) {
	chunks, oversized := splitByBudget(nil, 10_000, 20_000)

	if len(chunks) != 0 || len(oversized) != 0 {
		t.Fatalf("expected no envelopes and nothing oversized, got %v and %v", chunks, oversized)
	}
}

// enqueueOsVersion queues an OS version payload of roughly size bytes.
func enqueueOsVersion(t *testing.T, store *db.Store, id string, size int) {
	t.Helper()
	payload := fmt.Sprintf(`{"extra":"%s"}`, strings.Repeat("x", size))
	if err := store.EnqueueTelemetry(id, db.TelemetryOsVersion, payload); err != nil {
		t.Fatalf("enqueue %s: %v", id, err)
	}
}

// Intent: a backlog bigger than the server's limit, such as one built up during an outage, is delivered in one
// tick across several envelopes. Each envelope is under the 1 MB limit, and the items arrive in order.
func TestSendBatch_SplitsBacklogIntoEnvelopesUnderBudget(t *testing.T) {
	store := newTestStore(t)
	for i := 0; i < 6; i++ {
		enqueueOsVersion(t, store, fmt.Sprintf("backlog-%d", i), 300*1024)
	}

	var envelopes []*pb.TelemetryEnvelope
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		envelopes = append(envelopes, in)

		return &pb.TelemetryAck{Success: true}, nil
	})
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if len(envelopes) < 3 {
		t.Fatalf("expected the 1.8 MB backlog to need at least 3 envelopes, got %d", len(envelopes))
	}
	var delivered []string
	for i, env := range envelopes {
		if size := proto.Size(env); size >= 1024*1024 {
			t.Errorf("envelope %d is %d bytes, at or over the server's 1 MB limit", i, size)
		}
		for _, item := range env.Items {
			delivered = append(delivered, item.EventId)
		}
	}
	want := "backlog-0,backlog-1,backlog-2,backlog-3,backlog-4,backlog-5"
	if strings.Join(delivered, ",") != want {
		t.Errorf("expected items delivered in order %s, got %s", want, strings.Join(delivered, ","))
	}
	remaining, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(remaining) != 0 {
		t.Errorf("expected every item completed, %d still pending", len(remaining))
	}
}

// Intent: an item queued by an older agent, before the enqueue cap existed, that is too large for the server is
// dropped instead of blocking its tier forever. The items queued with it are still delivered.
func TestSendBatch_DiscardsItemTooLargeToSend(t *testing.T) {
	store := newTestStore(t)
	enqueueOsVersion(t, store, "before", 100)
	// Insert directly, as an older agent without the cap would have.
	huge := fmt.Sprintf(`{"extra":"%s"}`, strings.Repeat("x", 950*1024))
	if _, err := store.DB().Exec(
		`INSERT INTO telemetry_queue (id, item_type, payload, status, created_at, updated_at) VALUES (?, ?, ?, 0, ?, ?)`,
		"legacy-huge", int(db.TelemetryOsVersion), huge, "2026-01-01T00:00:01Z", "2026-01-01T00:00:01Z",
	); err != nil {
		t.Fatalf("insert legacy item: %v", err)
	}
	// Invalidate the cached count so the direct insert is seen.
	if err := store.ResetProcessingTelemetry(); err != nil {
		t.Fatalf("reset: %v", err)
	}

	var sent []string
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		for _, item := range in.Items {
			sent = append(sent, item.EventId)
		}

		return &pb.TelemetryAck{Success: true}, nil
	})
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if strings.Join(sent, ",") != "before" {
		t.Errorf("expected only %q sent, got %v", "before", sent)
	}
	// The oversized item must be gone, so a restart cannot bring it back either.
	if err := store.ResetProcessingTelemetry(); err != nil {
		t.Fatalf("reset: %v", err)
	}
	remaining, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(remaining) != 0 {
		t.Errorf("expected the oversized item discarded, found %d items", len(remaining))
	}
}

// Intent: one item bigger than the envelope budget but within the item limit is delivered on its own, not
// discarded.
func TestSendBatch_DeliversLoneItemBetweenBudgetAndLimit(t *testing.T) {
	store := newTestStore(t)
	enqueueOsVersion(t, store, "big-but-legal", 800*1024)

	var sent []string
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		if size := proto.Size(in); size >= 1024*1024 {
			t.Errorf("envelope is %d bytes, at or over the 1 MB limit", size)
		}
		for _, item := range in.Items {
			sent = append(sent, item.EventId)
		}

		return &pb.TelemetryAck{Success: true}, nil
	})
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if strings.Join(sent, ",") != "big-but-legal" {
		t.Errorf("expected the item delivered, got %v", sent)
	}
}

// Intent: when a later envelope in a tick fails, the envelopes already acknowledged stay completed. The failed
// envelope and every envelope after it return to pending, unsent, so the next tick retries them in order.
func TestSendBatch_LaterEnvelopeFails_RemainingItemsReturnToPending(t *testing.T) {
	store := newTestStore(t)
	for i := 0; i < 6; i++ {
		enqueueOsVersion(t, store, fmt.Sprintf("item-%d", i), 300*1024)
	}

	calls := 0
	var acked []string
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		calls++
		if calls == 1 {
			for _, item := range in.Items {
				acked = append(acked, item.EventId)
			}

			return &pb.TelemetryAck{Success: true}, nil
		}

		return &pb.TelemetryAck{Success: false, ErrorMessage: "rejected"}, nil
	})
	s := New(store, client, state.New(), jitter.NewDefault(), neverPaused())

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if calls != 2 {
		t.Errorf("expected sending to stop after the rejected envelope (2 calls), got %d", calls)
	}
	remaining, err := store.DequeueTelemetry(10)
	if err != nil {
		t.Fatalf("dequeue: %v", err)
	}
	if len(acked)+len(remaining) != 6 {
		t.Fatalf("expected acked plus pending to cover all 6 items, got %d acked and %d pending", len(acked), len(remaining))
	}
	for _, item := range remaining {
		for _, done := range acked {
			if item.ID == done {
				t.Errorf("item %s was acknowledged but is pending again", item.ID)
			}
		}
	}
}

// Intent: once an ack says the host is paused, the sender stops sending the rest of the tick's envelopes. The
// server would discard them anyway, and a paused host must go quiet.
func TestSendBatch_PauseAckStopsRemainingEnvelopes(t *testing.T) {
	store := newTestStore(t)
	for i := 0; i < 6; i++ {
		enqueueOsVersion(t, store, fmt.Sprintf("p-%d", i), 300*1024)
	}

	calls := 0
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		calls++

		return &pb.TelemetryAck{Success: true, Reason: pb.IngestRejectionReason_INGEST_REJECTION_REASON_HOST_PAUSED}, nil
	})
	recorder := newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), recorder.coord)

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if calls != 1 {
		t.Errorf("expected exactly one envelope before the pause stopped sending, got %d", calls)
	}
	if recorder.pauses.Load() != 1 {
		t.Errorf("expected the host to enter pause once, got %d", recorder.pauses.Load())
	}
	// The recorder's pause hook does not clear the queue, so the items of the envelopes that were never sent
	// must have been returned to pending rather than left in processing.
	if got := countByStatus(t, store, db.QueueStatusCompleted); got != 2 {
		t.Errorf("expected the 2 items of the acknowledged envelope to be completed, got %d", got)
	}
	if got := countByStatus(t, store, db.QueueStatusPending); got != 4 {
		t.Errorf("expected the 4 items of the unsent envelopes to be pending again, got %d", got)
	}
	if got := countByStatus(t, store, db.QueueStatusProcessing); got != 0 {
		t.Errorf("expected nothing left in processing, got %d", got)
	}
}

// countByStatus counts the queued rows in one status.
func countByStatus(t *testing.T, store *db.Store, status db.QueueStatus) int {
	t.Helper()
	var n int
	if err := store.DB().QueryRow("SELECT COUNT(*) FROM telemetry_queue WHERE status = ?", int(status)).Scan(&n); err != nil {
		t.Fatalf("count status %d: %v", status, err)
	}

	return n
}

// Intent: a pause that begins while an envelope is in flight, for example from a heartbeat ping, stops the
// remaining envelopes of the tick even though that envelope's own ack was plain. The envelope that was in flight
// stays completed and the ones not yet sent go back to pending.
func TestSendBatch_PauseDuringEnvelopeStopsRemainingEnvelopes(t *testing.T) {
	store := newTestStore(t)
	for i := 0; i < 6; i++ {
		enqueueOsVersion(t, store, fmt.Sprintf("mid-%d", i), 300*1024)
	}

	calls := 0
	var recorder *pauseRecorder
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		calls++
		if calls == 1 {
			recorder.coord.Observe(pause.HostPaused)
		}

		return &pb.TelemetryAck{Success: true}, nil
	})
	recorder = newPauseRecorder(nil)
	s := New(store, client, state.New(), jitter.NewDefault(), recorder.coord)

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if calls != 1 {
		t.Errorf("expected sending to stop after the envelope that was in flight when the pause began, got %d calls", calls)
	}
	if recorder.pauses.Load() != 1 {
		t.Errorf("expected the host to enter pause once, got %d", recorder.pauses.Load())
	}
	if got := countByStatus(t, store, db.QueueStatusCompleted); got != 2 {
		t.Errorf("expected the 2 items of the sent envelope to be completed, got %d", got)
	}
	if got := countByStatus(t, store, db.QueueStatusPending); got != 4 {
		t.Errorf("expected the 4 items of the unsent envelopes to be pending again, got %d", got)
	}
	if got := countByStatus(t, store, db.QueueStatusProcessing); got != 0 {
		t.Errorf("expected nothing left in processing, got %d", got)
	}
}

// Intent: when the pause clears the queue, as the real pause hook does, the envelopes that were not sent are not
// resurrected by being marked pending: the queue stays empty.
func TestSendBatch_PauseDuringEnvelopeWithClearedQueue_StaysEmpty(t *testing.T) {
	store := newTestStore(t)
	for i := 0; i < 6; i++ {
		enqueueOsVersion(t, store, fmt.Sprintf("cleared-%d", i), 300*1024)
	}

	calls := 0
	var recorder *pauseRecorder
	client := unaryOnlyClient(func(in *pb.TelemetryEnvelope) (*pb.TelemetryAck, error) {
		calls++
		if calls == 1 {
			recorder.coord.Observe(pause.HostPaused)
		}

		return &pb.TelemetryAck{Success: true}, nil
	})
	recorder = newPauseRecorder(func(pause.Reason) {
		if err := store.ClearTelemetryQueue(); err != nil {
			t.Errorf("clear queue: %v", err)
		}
	})
	s := New(store, client, state.New(), jitter.NewDefault(), recorder.coord)

	s.sendBatch(context.Background(), "slow", SlowTypes)

	if calls != 1 {
		t.Errorf("expected exactly one envelope sent, got %d", calls)
	}
	if n, err := store.CountPendingTelemetry(); err != nil || n != 0 {
		t.Errorf("expected an empty queue after the pause cleared it, got %d pending (err %v)", n, err)
	}
	if got := countByStatus(t, store, db.QueueStatusPending) + countByStatus(t, store, db.QueueStatusProcessing); got != 0 {
		t.Errorf("expected no rows left in the queue, got %d", got)
	}
}
