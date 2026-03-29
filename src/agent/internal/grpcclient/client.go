// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package grpcclient provides a gRPC client for communicating with the vord server.
package grpcclient

import (
	"context"
	"crypto/tls"
	"fmt"
	"strconv"
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

// New creates a new gRPC client connected to the specified target.
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
			MinVersion: tls.VersionTLS12,
		})))
	} else {
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