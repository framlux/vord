// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/framlux/vord/internal/commands"
	pb "github.com/framlux/vord/internal/proto/agent"
	"github.com/framlux/vord/internal/state"
	"google.golang.org/grpc"
)

// fakeConfigurationClient stands in for the server's configuration service, answering the pending commands it
// is given and recording what the agent asks and acknowledges.
type fakeConfigurationClient struct {
	pb.ConfigurationClient

	pending []*pb.AgentCommand
	pollErr error

	polls []*pb.GetPendingCommandsRequest
	acks  []*pb.AcknowledgeCommandRequest
}

func (f *fakeConfigurationClient) GetPendingCommands(_ context.Context, in *pb.GetPendingCommandsRequest, _ ...grpc.CallOption) (*pb.GetPendingCommandsResponse, error) {
	f.polls = append(f.polls, in)
	if f.pollErr != nil {
		return nil, f.pollErr
	}

	return &pb.GetPendingCommandsResponse{Commands: f.pending}, nil
}

func (f *fakeConfigurationClient) AcknowledgeCommand(_ context.Context, in *pb.AcknowledgeCommandRequest, _ ...grpc.CallOption) (*pb.AcknowledgeCommandResponse, error) {
	f.acks = append(f.acks, in)

	return &pb.AcknowledgeCommandResponse{}, nil
}

func newPollTestState() *state.RuntimeState {
	rs := state.New()
	rs.SetMachineID(200)
	rs.SetTenantID(100)

	return rs
}

func always() bool { return true }
func never() bool  { return false }

// Intent: A host that allows remote commands polls on every tick without ever looking at the reporter record.
func TestCommandPollGate_RemoteCommandsOn_AlwaysPollsWithoutTheRecordCheck(t *testing.T) {
	checks := 0
	gate := commandPollGate(true, func() bool {
		checks++

		return false
	})

	if gate() == false {
		t.Error("a host with remote commands on did not poll")
	}

	if checks != 0 {
		t.Errorf("the record was checked %d times, want 0 when remote commands are on", checks)
	}
}

// Intent: A host with remote commands off polls only while it holds a reporter record, and the record is read
// on every tick so a cleanup that removes it ends the polling.
func TestCommandPollGate_RemoteCommandsOff_FollowsTheRecord(t *testing.T) {
	held := true
	gate := commandPollGate(false, func() bool { return held })

	if gate() == false {
		t.Error("a host holding a reporter record did not poll")
	}

	held = false

	if gate() {
		t.Error("a host with no reporter record kept polling")
	}

	held = true

	if gate() == false {
		t.Error("a host that regained a reporter record did not poll again")
	}
}

// Intent: A tick asks the server for commands when the gate allows it, and makes no call when the gate refuses,
// which is what keeps a never-enrolled host with remote commands off from adding to the fleet's poll load.
func TestCommandPollTick_CallsTheServerOnlyWhenTheGateAllows(t *testing.T) {
	cases := []struct {
		name       string
		paused     func() bool
		shouldPoll func() bool
		wantPolls  int
	}{
		{name: "gate open", paused: never, shouldPoll: always, wantPolls: 1},
		{name: "gate closed", paused: never, shouldPoll: never, wantPolls: 0},
		{name: "paused with gate open", paused: always, shouldPoll: always, wantPolls: 0},
		{name: "paused with gate closed", paused: always, shouldPoll: never, wantPolls: 0},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			client := &fakeConfigurationClient{}

			commandPollTick(context.Background(), client, nil, newPollTestState(), tc.paused, tc.shouldPoll)

			if len(client.polls) != tc.wantPolls {
				t.Fatalf("the server was polled %d times, want %d", len(client.polls), tc.wantPolls)
			}

			if (tc.wantPolls == 1) && (client.polls[0].MachineId != 200) {
				t.Errorf("polled for machine %d, want 200", client.polls[0].MachineId)
			}
		})
	}
}

// Intent: A poll that fails changes nothing and does not panic, so the next tick tries again.
func TestCommandPollTick_AFailedPollIsTolerated(t *testing.T) {
	client := &fakeConfigurationClient{pollErr: errors.New("unavailable")}

	commandPollTick(context.Background(), client, nil, newPollTestState(), never, always)

	if len(client.polls) != 1 {
		t.Errorf("the server was polled %d times, want 1", len(client.polls))
	}
}

// Intent: End to end through the poll, with remote commands off a reboot that the server delivers is
// acknowledged as rejected for the flag and never reaches an executor, while the agent still polled because it
// holds a reporter record.
func TestCommandPollTick_RemoteCommandsOff_RebootIsRejectedNotExecuted(t *testing.T) {
	store := newTestStore(t)
	client := &fakeConfigurationClient{
		pending: []*pb.AgentCommand{{
			Id:               "cmd-1",
			Type:             "reboot",
			CanonicalPayload: "{}",
			Signature:        []byte("unsigned"),
			SigningKeyId:     1,
			TenantId:         100,
			MachineId:        200,
			UserId:           42,
		}},
	}

	processor := commands.NewProcessor(
		commands.NewHandler(commands.HandlerDeps{}),
		&storeNonceAdapter{store: store},
		&grpcAckAdapter{client: client},
		time.Hour,
		commands.ProcessorOptions{AllowAll: false, LocalRecordPresent: always},
	)

	commandPollTick(context.Background(), client, processor, newPollTestState(), never, commandPollGate(false, always))

	if len(client.polls) != 1 {
		t.Fatalf("the server was polled %d times, want 1", len(client.polls))
	}

	if len(client.acks) != 1 {
		t.Fatalf("got %d acknowledgements, want exactly the rejection", len(client.acks))
	}

	ack := client.acks[0]
	if (ack.CommandId != "cmd-1") || (ack.Result.ResultType != pb.ResultType_REJECTED) || (ack.Result.Message != "remote commands are disabled on this host") || ack.Result.Success {
		t.Errorf("acknowledgement = %+v, want cmd-1 rejected with the remote-commands-disabled message", ack)
	}
}
