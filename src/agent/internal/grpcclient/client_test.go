// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package grpcclient

import (
	"context"
	"testing"

	"github.com/framlux/vord/internal/state"
	"google.golang.org/grpc"
	"google.golang.org/grpc/metadata"
)

// capturedUnaryContext is set by the mock invoker so tests can inspect the
// outgoing metadata that the interceptor attached.
func makeCapturingInvoker(captured *context.Context) grpc.UnaryInvoker {
	return func(
		ctx context.Context,
		method string,
		req, reply any,
		cc *grpc.ClientConn,
		opts ...grpc.CallOption,
	) error {
		*captured = ctx

		return nil
	}
}

// makeCapturingStreamer returns a mock Streamer that records the context it
// received so tests can verify outgoing metadata.
func makeCapturingStreamer(captured *context.Context) grpc.Streamer {
	return func(
		ctx context.Context,
		desc *grpc.StreamDesc,
		cc *grpc.ClientConn,
		method string,
		opts ...grpc.CallOption,
	) (grpc.ClientStream, error) {
		*captured = ctx

		return nil, nil
	}
}

// extractOutgoingMD is a helper that pulls metadata from the captured context.
func extractOutgoingMD(t *testing.T, ctx context.Context) metadata.MD {
	t.Helper()
	md, ok := metadata.FromOutgoingContext(ctx)
	if ok == false {
		t.Fatal("expected outgoing metadata on context but found none")
	}

	return md
}

// --- Unary interceptor tests ---

func TestUnaryInterceptor_InjectsApiKeyAndMachineID(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("test-key-abc")
	runtimeState.SetMachineID(42)

	interceptor := metadataInterceptor(runtimeState)

	var captured context.Context
	invoker := makeCapturingInvoker(&captured)

	err := interceptor(context.Background(), "/test.Service/Method", nil, nil, nil, invoker)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "test-key-abc" {
		t.Errorf("expected x-api-key [test-key-abc], got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "42" {
		t.Errorf("expected x-machine-id [42], got %v", machineIDs)
	}
}

func TestUnaryInterceptor_OmitsApiKeyWhenEmpty(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("")
	runtimeState.SetMachineID(10)

	interceptor := metadataInterceptor(runtimeState)

	var captured context.Context
	invoker := makeCapturingInvoker(&captured)

	err := interceptor(context.Background(), "/test.Service/Method", nil, nil, nil, invoker)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 0 {
		t.Errorf("expected x-api-key to be absent, got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "10" {
		t.Errorf("expected x-machine-id [10], got %v", machineIDs)
	}
}

func TestUnaryInterceptor_OmitsMachineIDWhenZero(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("key-xyz")
	runtimeState.SetMachineID(0)

	interceptor := metadataInterceptor(runtimeState)

	var captured context.Context
	invoker := makeCapturingInvoker(&captured)

	err := interceptor(context.Background(), "/test.Service/Method", nil, nil, nil, invoker)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 0 {
		t.Errorf("expected x-machine-id to be absent, got %v", machineIDs)
	}

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "key-xyz" {
		t.Errorf("expected x-api-key [key-xyz], got %v", apiKeys)
	}
}

func TestUnaryInterceptor_PreservesExistingMetadata(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("new-key")
	runtimeState.SetMachineID(99)

	interceptor := metadataInterceptor(runtimeState)

	// Attach pre-existing metadata to the outgoing context.
	existingMD := metadata.Pairs("x-custom-header", "custom-value", "x-trace-id", "trace-123")
	ctx := metadata.NewOutgoingContext(context.Background(), existingMD)

	var captured context.Context
	invoker := makeCapturingInvoker(&captured)

	err := interceptor(ctx, "/test.Service/Method", nil, nil, nil, invoker)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	// Verify pre-existing headers are preserved.
	customValues := md.Get("x-custom-header")
	if len(customValues) != 1 || customValues[0] != "custom-value" {
		t.Errorf("expected x-custom-header [custom-value], got %v", customValues)
	}

	traceValues := md.Get("x-trace-id")
	if len(traceValues) != 1 || traceValues[0] != "trace-123" {
		t.Errorf("expected x-trace-id [trace-123], got %v", traceValues)
	}

	// Verify injected headers are also present.
	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "new-key" {
		t.Errorf("expected x-api-key [new-key], got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "99" {
		t.Errorf("expected x-machine-id [99], got %v", machineIDs)
	}
}

func TestUnaryInterceptor_OmitsBothWhenDefaultState(t *testing.T) {
	runtimeState := state.New()

	interceptor := metadataInterceptor(runtimeState)

	var captured context.Context
	invoker := makeCapturingInvoker(&captured)

	err := interceptor(context.Background(), "/test.Service/Method", nil, nil, nil, invoker)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md, ok := metadata.FromOutgoingContext(captured)
	if ok == false {
		// When both are omitted the context still has metadata (empty MD),
		// but neither key should be present.
		return
	}

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 0 {
		t.Errorf("expected x-api-key to be absent, got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 0 {
		t.Errorf("expected x-machine-id to be absent, got %v", machineIDs)
	}
}

// --- Stream interceptor tests ---

func TestStreamInterceptor_InjectsApiKeyAndMachineID(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("stream-key-abc")
	runtimeState.SetMachineID(77)

	interceptor := streamMetadataInterceptor(runtimeState)

	var captured context.Context
	streamer := makeCapturingStreamer(&captured)

	_, err := interceptor(context.Background(), &grpc.StreamDesc{}, nil, "/test.Service/Stream", streamer)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "stream-key-abc" {
		t.Errorf("expected x-api-key [stream-key-abc], got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "77" {
		t.Errorf("expected x-machine-id [77], got %v", machineIDs)
	}
}

func TestStreamInterceptor_OmitsApiKeyWhenEmpty(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("")
	runtimeState.SetMachineID(5)

	interceptor := streamMetadataInterceptor(runtimeState)

	var captured context.Context
	streamer := makeCapturingStreamer(&captured)

	_, err := interceptor(context.Background(), &grpc.StreamDesc{}, nil, "/test.Service/Stream", streamer)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 0 {
		t.Errorf("expected x-api-key to be absent, got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "5" {
		t.Errorf("expected x-machine-id [5], got %v", machineIDs)
	}
}

func TestStreamInterceptor_OmitsMachineIDWhenZero(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("stream-key-xyz")
	runtimeState.SetMachineID(0)

	interceptor := streamMetadataInterceptor(runtimeState)

	var captured context.Context
	streamer := makeCapturingStreamer(&captured)

	_, err := interceptor(context.Background(), &grpc.StreamDesc{}, nil, "/test.Service/Stream", streamer)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 0 {
		t.Errorf("expected x-machine-id to be absent, got %v", machineIDs)
	}

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "stream-key-xyz" {
		t.Errorf("expected x-api-key [stream-key-xyz], got %v", apiKeys)
	}
}

func TestStreamInterceptor_PreservesExistingMetadata(t *testing.T) {
	runtimeState := state.New()
	runtimeState.SetApiKey("stream-new-key")
	runtimeState.SetMachineID(101)

	interceptor := streamMetadataInterceptor(runtimeState)

	existingMD := metadata.Pairs("x-custom-header", "stream-custom", "x-request-id", "req-456")
	ctx := metadata.NewOutgoingContext(context.Background(), existingMD)

	var captured context.Context
	streamer := makeCapturingStreamer(&captured)

	_, err := interceptor(ctx, &grpc.StreamDesc{}, nil, "/test.Service/Stream", streamer)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md := extractOutgoingMD(t, captured)

	// Verify pre-existing headers are preserved.
	customValues := md.Get("x-custom-header")
	if len(customValues) != 1 || customValues[0] != "stream-custom" {
		t.Errorf("expected x-custom-header [stream-custom], got %v", customValues)
	}

	requestIDs := md.Get("x-request-id")
	if len(requestIDs) != 1 || requestIDs[0] != "req-456" {
		t.Errorf("expected x-request-id [req-456], got %v", requestIDs)
	}

	// Verify injected headers are also present.
	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 1 || apiKeys[0] != "stream-new-key" {
		t.Errorf("expected x-api-key [stream-new-key], got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 1 || machineIDs[0] != "101" {
		t.Errorf("expected x-machine-id [101], got %v", machineIDs)
	}
}

// --- H2: plaintext-target safety tests ---

func TestAssertPlaintextTargetIsSafe_LoopbackV4_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("127.0.0.1:12234"); err != nil {
		t.Fatalf("expected loopback v4 to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_LoopbackV6_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("[::1]:12234"); err != nil {
		t.Fatalf("expected loopback v6 to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_LocalhostHostname_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("localhost:12234"); err != nil {
		t.Fatalf("expected localhost to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_RFC1918_Class_A_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("10.0.0.5:12234"); err != nil {
		t.Fatalf("expected 10.0.0.5 (private) to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_RFC1918_Class_B_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("172.20.0.1:12234"); err != nil {
		t.Fatalf("expected 172.20.0.1 (private) to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_RFC1918_Class_C_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("192.168.1.1:12234"); err != nil {
		t.Fatalf("expected 192.168.1.1 (private) to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_RFC4193_ULA_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("[fc00::1]:12234"); err != nil {
		t.Fatalf("expected fc00::1 (ULA) to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_PublicIPv4_Refused(t *testing.T) {
	err := assertPlaintextTargetIsSafe("203.0.113.5:12234")
	if err == nil {
		t.Fatal("expected public IPv4 to be refused, got nil error")
	}
}

func TestAssertPlaintextTargetIsSafe_PublicIPv6_Refused(t *testing.T) {
	err := assertPlaintextTargetIsSafe("[2001:db8::1]:12234")
	if err == nil {
		t.Fatal("expected public IPv6 to be refused, got nil error")
	}
}

func TestAssertPlaintextTargetIsSafe_DnsSchemeStripped(t *testing.T) {
	// dns:/// scheme with loopback target should be allowed after scheme strip.
	if err := assertPlaintextTargetIsSafe("dns:///127.0.0.1:12234"); err != nil {
		t.Fatalf("expected dns:/// scheme with loopback to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_EmptyTarget_Refused(t *testing.T) {
	if err := assertPlaintextTargetIsSafe(""); err == nil {
		t.Fatal("expected empty target to be refused, got nil error")
	}
}

func TestAssertPlaintextTargetIsSafe_NoPortLocalhost_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("localhost"); err != nil {
		t.Fatalf("expected bare localhost to be allowed, got %v", err)
	}
}

func TestAssertPlaintextTargetIsSafe_BareLoopbackIp_Allowed(t *testing.T) {
	if err := assertPlaintextTargetIsSafe("127.0.0.1"); err != nil {
		t.Fatalf("expected bare 127.0.0.1 to be allowed, got %v", err)
	}
}

func TestParseHostForCheck_StripsIPv6Brackets(t *testing.T) {
	host, err := parseHostForCheck("[fe80::1]:12234")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if host != "fe80::1" {
		t.Errorf("expected fe80::1 host, got %q", host)
	}
}

func TestParseHostForCheck_StripsDnsScheme(t *testing.T) {
	host, err := parseHostForCheck("dns:///server.internal:12234")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if host != "server.internal" {
		t.Errorf("expected server.internal host, got %q", host)
	}
}

func TestStreamInterceptor_OmitsBothWhenDefaultState(t *testing.T) {
	runtimeState := state.New()

	interceptor := streamMetadataInterceptor(runtimeState)

	var captured context.Context
	streamer := makeCapturingStreamer(&captured)

	_, err := interceptor(context.Background(), &grpc.StreamDesc{}, nil, "/test.Service/Stream", streamer)
	if err != nil {
		t.Fatalf("interceptor returned unexpected error: %v", err)
	}

	md, ok := metadata.FromOutgoingContext(captured)
	if ok == false {
		return
	}

	apiKeys := md.Get("x-api-key")
	if len(apiKeys) != 0 {
		t.Errorf("expected x-api-key to be absent, got %v", apiKeys)
	}

	machineIDs := md.Get("x-machine-id")
	if len(machineIDs) != 0 {
		t.Errorf("expected x-machine-id to be absent, got %v", machineIDs)
	}
}
