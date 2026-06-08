// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package grpcclient provides a gRPC client for communicating with the vord server.
package grpcclient

import (
	"context"
	"crypto/tls"
	"fmt"
	"log/slog"
	"net"
	"strconv"
	"strings"
	"time"

	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/grpc/metadata"
)

// Client wraps gRPC connections to the vord server.
type Client struct {
	conn           *grpc.ClientConn
	Registration   pb.RegistrationClient
	Configuration  pb.ConfigurationClient
	Telemetry      pb.TelemetryClient
	state          *state.RuntimeState
}

// New creates a new gRPC client connected to the specified target. When useTLS is false the
// target is validated against assertPlaintextTargetIsSafe — plaintext connections to public
// IPs are refused outright so a misconfigured agent cannot ship its API key in clear over the
// public internet. Loopback and RFC 1918 / RFC 4193 addresses are allowed.
func New(target string, runtimeState *state.RuntimeState, useTLS bool) (*Client, error) {
	opts := []grpc.DialOption{
		grpc.WithUnaryInterceptor(metadataInterceptor(runtimeState)),
		grpc.WithStreamInterceptor(streamMetadataInterceptor(runtimeState)),
		grpc.WithKeepaliveParams(keepalive.ClientParameters{
			Time:                30 * time.Second,
			Timeout:             10 * time.Second,
			PermitWithoutStream: true,
		}),
	}

	if useTLS {
		opts = append(opts, grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{
			MinVersion: tls.VersionTLS13,
		})))
	} else {
		if err := assertPlaintextTargetIsSafe(target); err != nil {
			return nil, err
		}
		slog.Warn("TLS disabled - gRPC connection is unencrypted, API key will be sent in plaintext",
			"target", target)
		opts = append(opts, grpc.WithTransportCredentials(insecure.NewCredentials()))
	}

	conn, err := grpc.NewClient(target, opts...)
	if err != nil {
		return nil, fmt.Errorf("connecting to gRPC server %s: %w", target, err)
	}

	return &Client{
		conn:          conn,
		Registration:  pb.NewRegistrationClient(conn),
		Configuration: pb.NewConfigurationClient(conn),
		Telemetry:     pb.NewTelemetryClient(conn),
		state:         runtimeState,
	}, nil
}

// Close closes the underlying gRPC connection.
func (c *Client) Close() error {
	return c.conn.Close()
}

// Conn returns the underlying gRPC client connection.
func (c *Client) Conn() *grpc.ClientConn {
	return c.conn
}

// metadataInterceptor adds machine identity headers to every gRPC call.
func metadataInterceptor(runtimeState *state.RuntimeState) grpc.UnaryClientInterceptor {
	return func(
		ctx context.Context,
		method string,
		req, reply any,
		cc *grpc.ClientConn,
		invoker grpc.UnaryInvoker,
		opts ...grpc.CallOption,
	) error {
		md, ok := metadata.FromOutgoingContext(ctx)
		if ok == false {
			md = metadata.MD{}
		} else {
			md = md.Copy()
		}

		if apiKey := runtimeState.ApiKey(); apiKey != "" {
			md.Set("x-api-key", apiKey)
		}
		if machineID := runtimeState.MachineID(); machineID > 0 {
			md.Set("x-machine-id", strconv.FormatInt(machineID, 10))
		}

		ctx = metadata.NewOutgoingContext(ctx, md)
		return invoker(ctx, method, req, reply, cc, opts...)
	}
}

// assertPlaintextTargetIsSafe returns an error if `target` resolves to any address that is
// not loopback, RFC 1918, RFC 4193, or link-local. The check is intentionally strict — if any
// resolved IP is public, the call is refused even if other resolved IPs are private, because
// gRPC may dial any of them depending on the resolver and load balancer.
//
// Host parsing is tolerant of:
//
//   - bare host: "vord.internal"
//   - host:port: "vord.internal:12234"
//   - IPv6 host: "[::1]" or "[fc00::1]:12234"
//   - dns:/// or unix:/// scheme prefixes (passed through with port stripping)
//
// If the host is not an IP literal, it is resolved via DNS; all returned IPs must be private
// for the call to succeed.
func assertPlaintextTargetIsSafe(target string) error {
	host, err := parseHostForCheck(target)
	if err != nil {
		return fmt.Errorf("plaintext gRPC target %q: %w", target, err)
	}

	// Accept the conventional "localhost" hostname without a DNS round-trip.
	if strings.EqualFold(host, "localhost") {
		return nil
	}

	var ips []net.IP
	if literal := net.ParseIP(host); literal != nil {
		ips = []net.IP{literal}
	} else {
		resolved, lookupErr := net.LookupIP(host)
		if lookupErr != nil {
			return fmt.Errorf("plaintext gRPC target %q DNS lookup failed: %w", target, lookupErr)
		}
		ips = resolved
	}

	for _, ip := range ips {
		if isPrivateOrLoopback(ip) == false {
			return fmt.Errorf(
				"plaintext gRPC refused: target %q resolves to non-private IP %s. "+
					"Set useTLS=true or restrict the target to a loopback/private address",
				target, ip)
		}
	}

	return nil
}

// parseHostForCheck extracts the host portion of a gRPC target, stripping any scheme prefix
// (dns:///, passthrough:///, etc.) and any trailing :port.
func parseHostForCheck(target string) (string, error) {
	t := target
	// Strip gRPC scheme prefixes per https://github.com/grpc/grpc/blob/master/doc/naming.md.
	if idx := strings.Index(t, ":///"); idx != -1 {
		t = t[idx+len(":///"):]
	}
	if t == "" {
		return "", fmt.Errorf("empty host")
	}

	// IPv6 literal with optional port: [::1] or [::1]:12234
	if strings.HasPrefix(t, "[") {
		closing := strings.Index(t, "]")
		if closing == -1 {
			return "", fmt.Errorf("malformed IPv6 host")
		}

		return t[1:closing], nil
	}

	// IPv4 / hostname with optional port
	if host, _, splitErr := net.SplitHostPort(t); splitErr == nil {
		return host, nil
	}

	return t, nil
}

// isPrivateOrLoopback returns true when ip is loopback, link-local, or in an RFC-private range.
// IPv4 ranges: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8.
// IPv6 ranges: ::1 (loopback), fc00::/7 (ULA), fe80::/10 (link-local).
func isPrivateOrLoopback(ip net.IP) bool {
	if ip == nil {
		return false
	}
	if ip.IsLoopback() || ip.IsLinkLocalUnicast() || ip.IsPrivate() {
		return true
	}

	return false
}

// streamMetadataInterceptor adds machine identity headers to streaming gRPC calls.
func streamMetadataInterceptor(runtimeState *state.RuntimeState) grpc.StreamClientInterceptor {
	return func(
		ctx context.Context,
		desc *grpc.StreamDesc,
		cc *grpc.ClientConn,
		method string,
		streamer grpc.Streamer,
		opts ...grpc.CallOption,
	) (grpc.ClientStream, error) {
		md, ok := metadata.FromOutgoingContext(ctx)
		if ok == false {
			md = metadata.MD{}
		} else {
			md = md.Copy()
		}

		if apiKey := runtimeState.ApiKey(); apiKey != "" {
			md.Set("x-api-key", apiKey)
		}
		if machineID := runtimeState.MachineID(); machineID > 0 {
			md.Set("x-machine-id", strconv.FormatInt(machineID, 10))
		}

		ctx = metadata.NewOutgoingContext(ctx, md)
		return streamer(ctx, desc, cc, method, opts...)
	}
}