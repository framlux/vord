// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package main

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kubereporter"
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
	}, func() bool {
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
	gate := commandPollGate(false, func() bool { return held }, never)

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
		commands.ProcessorOptions{AllowAll: false, LocalRecordPresent: always, CopiedRecordPresent: never},
	)

	commandPollTick(context.Background(), client, processor, newPollTestState(), never, commandPollGate(false, always, never))

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

// admissionHost is a host wired the way main wires it: the real reporter-record check on a temporary data
// directory, a processor built from the admission options, and a fake server that records what the host answers.
type admissionHost struct {
	t          *testing.T
	dataDir    string
	client     *fakeConfigurationClient
	processor  *commands.Processor
	shouldPoll func() bool
	options    commands.ProcessorOptions
}

func newAdmissionHost(t *testing.T, allowRemoteCommands bool) *admissionHost {
	t.Helper()

	h := &admissionHost{t: t, dataDir: t.TempDir(), client: &fakeConfigurationClient{}}
	h.options, h.shouldPoll = newCommandAdmission(allowRemoteCommands, h.dataDir)
	h.processor = commands.NewProcessor(
		commands.NewHandler(commands.HandlerDeps{}),
		&storeNonceAdapter{store: newTestStore(t)},
		&grpcAckAdapter{client: h.client},
		time.Hour,
		h.options,
	)

	return h
}

// enrolLocally leaves the record an enrolled host holds.
func (h *admissionHost) enrolLocally() {
	h.t.Helper()

	err := kubereporter.SaveState(h.dataDir, kubereporter.State{
		InstanceID: "0123456789abcdef",
		Server:     "https://10.0.0.1:6443",
		CACert:     "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
		Token:      "token",
	})
	if err != nil {
		h.t.Fatalf("SaveState: %v", err)
	}
}

// answerTo hands the processor an unsigned command of the given type, as if the server had delivered it, and
// returns the single acknowledgement message. The processor is asked directly, not through the poll, so a host
// whose gate keeps it from polling is still shown to refuse a command that reaches it. A command the processor
// lets past its remote-commands check dies at the next one, because the fake key store holds no key, so
// "unknown signing key" means the command was admitted and the flag text means it was not.
func (h *admissionHost) answerTo(commandType commands.CommandType) string {
	h.t.Helper()

	h.client.acks = nil

	h.processor.Process(context.Background(), commands.PendingCommand{
		ID:               "cmd-1",
		Type:             string(commandType),
		CanonicalPayload: "{}",
		Signature:        []byte("unsigned"),
		SigningKeyID:     1,
		TenantID:         100,
		MachineID:        200,
		UserID:           42,
	}, 100, 200)

	if len(h.client.acks) != 1 {
		h.t.Fatalf("got %d acknowledgements for %s, want 1", len(h.client.acks), commandType)
	}

	return h.client.acks[0].Result.Message
}

const (
	messageAdmitted = "unknown signing key"
	messageRefused  = "remote commands are disabled on this host"
)

// Intent: A host that allows remote commands polls and admits every command whether or not it ever enrolled.
func TestCommandAdmission_RemoteCommandsOn_AdmitsEverythingWithoutARecord(t *testing.T) {
	h := newAdmissionHost(t, true)

	if h.options.AllowAll == false {
		t.Error("AllowAll = false, want true when remote commands are on")
	}

	if h.shouldPoll() == false {
		t.Error("a host with remote commands on did not poll")
	}

	for _, commandType := range []commands.CommandType{commands.CommandReboot, commands.CommandK8sReporterEnrol, commands.CommandK8sReporterUninstall} {
		if got := h.answerTo(commandType); got != messageAdmitted {
			t.Errorf("%s answered %q, want it admitted (%q)", commandType, got, messageAdmitted)
		}
	}
}

// Intent: A host with remote commands off that never enrolled neither polls nor admits anything, the uninstall
// command included, so its admin kubeconfig is never used on its behalf.
func TestCommandAdmission_RemoteCommandsOff_WithoutAStateFile_PollsAndAdmitsNothing(t *testing.T) {
	h := newAdmissionHost(t, false)

	if h.options.AllowAll {
		t.Error("AllowAll = true, want false when remote commands are off")
	}

	if h.shouldPoll() {
		t.Error("a host with remote commands off and no state file polled")
	}

	for _, commandType := range []commands.CommandType{commands.CommandReboot, commands.CommandK8sReporterEnrol, commands.CommandK8sReporterUnenrol, commands.CommandK8sReporterUninstall} {
		if got := h.answerTo(commandType); got != messageRefused {
			t.Errorf("%s answered %q, want it refused (%q)", commandType, got, messageRefused)
		}
	}
}

// Intent: Once the state file is there, the same host polls and admits the two cleanup commands and nothing
// else, and when the state file goes (as it does at the end of a cleanup) it polls and admits nothing again.
func TestCommandAdmission_RemoteCommandsOff_WithAStateFile_AdmitsCleanupOnly(t *testing.T) {
	h := newAdmissionHost(t, false)
	h.enrolLocally()

	if h.shouldPoll() == false {
		t.Fatal("a host with remote commands off and a state file did not poll")
	}

	want := map[commands.CommandType]string{
		commands.CommandK8sReporterUnenrol:   messageAdmitted,
		commands.CommandK8sReporterUninstall: messageAdmitted,
		commands.CommandK8sReporterEnrol:     messageRefused,
		commands.CommandReboot:               messageRefused,
		commands.CommandKillProcess:          messageRefused,
	}
	for commandType, message := range want {
		if got := h.answerTo(commandType); got != message {
			t.Errorf("%s answered %q, want %q", commandType, got, message)
		}
	}

	if err := kubereporter.DeleteState(h.dataDir); err != nil {
		t.Fatal(err)
	}

	if h.shouldPoll() {
		t.Error("the host kept polling after its state file was removed")
	}

	if got := h.answerTo(commands.CommandK8sReporterUninstall); got != messageRefused {
		t.Errorf("uninstall answered %q after the state file was removed, want it refused", got)
	}
}

// writePending leaves the file an enrol that created its Secret and never wrote the state file leaves behind. It
// names no host machine id, so the host the test runs on is never taken for another.
func (h *admissionHost) writePending() string {
	h.t.Helper()

	path := filepath.Join(h.dataDir, kubereporter.PendingFileName)
	if err := os.WriteFile(path, []byte(`{"instance_id":"0123456789abcdef","host_machine_id":""}`), 0o600); err != nil {
		h.t.Fatal(err)
	}

	return path
}

// Intent: A host whose enrol was cut off after it created its token Secret holds no state file but does hold a
// record of the Secret. With remote commands off it still polls and admits the two cleanup commands and nothing
// else, and it stops when the pending file goes, as it does at the end of a cleanup.
func TestCommandAdmission_RemoteCommandsOff_WithOnlyAPendingFile_AdmitsCleanupOnly(t *testing.T) {
	h := newAdmissionHost(t, false)
	pending := h.writePending()

	if h.shouldPoll() == false {
		t.Fatal("a host with remote commands off and a pending file did not poll")
	}

	want := map[commands.CommandType]string{
		commands.CommandK8sReporterUnenrol:   messageAdmitted,
		commands.CommandK8sReporterUninstall: messageAdmitted,
		commands.CommandK8sReporterEnrol:     messageRefused,
		commands.CommandReboot:               messageRefused,
	}
	for commandType, message := range want {
		if got := h.answerTo(commandType); got != message {
			t.Errorf("%s answered %q, want %q", commandType, got, message)
		}
	}

	if err := os.Remove(pending); err != nil {
		t.Fatal(err)
	}

	if h.shouldPoll() {
		t.Error("the host kept polling after its pending file was removed")
	}

	if got := h.answerTo(commands.CommandK8sReporterUninstall); got != messageRefused {
		t.Errorf("uninstall answered %q after the pending file was removed, want it refused", got)
	}
}

// Intent: A host with remote commands off polls while it holds a record written on another host too, because it
// can unenrol it, and stops when neither kind of record is left.
func TestCommandPollGate_RemoteCommandsOff_PollsForACopiedRecord(t *testing.T) {
	own, copied := false, true
	gate := commandPollGate(false, func() bool { return own }, func() bool { return copied })

	if gate() == false {
		t.Error("a host holding only a copied record did not poll")
	}

	copied = false

	if gate() {
		t.Error("a host holding no record kept polling")
	}

	own = true

	if gate() == false {
		t.Error("a host holding its own record did not poll")
	}
}

// newAdmissionHostFrom builds an admission host whose two record checks are answered by the test.
func newAdmissionHostFrom(t *testing.T, allowRemoteCommands bool, own *bool, copied *bool) *admissionHost {
	t.Helper()

	h := &admissionHost{t: t, dataDir: t.TempDir(), client: &fakeConfigurationClient{}}
	h.options, h.shouldPoll = commandAdmissionFrom(allowRemoteCommands, func() bool { return *own }, func() bool { return *copied })
	h.processor = commands.NewProcessor(
		commands.NewHandler(commands.HandlerDeps{}),
		&storeNonceAdapter{store: newTestStore(t)},
		&grpcAckAdapter{client: h.client},
		time.Hour,
		h.options,
	)

	return h
}

// Intent: A clone with remote commands off, whose only record was written on another host, can be unenrolled,
// because that removes local files and nothing in the cluster, but it never takes an uninstall: it never
// enrolled, so its opt-out holds and its admin credential is not used on its behalf. It polls to receive the
// unenrol, and every other command is refused as before.
func TestCommandAdmission_RemoteCommandsOff_WithOnlyACopiedRecord_AdmitsUnenrolOnly(t *testing.T) {
	own, copied := false, true
	h := newAdmissionHostFrom(t, false, &own, &copied)

	if h.shouldPoll() == false {
		t.Fatal("a host with remote commands off and a copied record did not poll")
	}

	want := map[commands.CommandType]string{
		commands.CommandK8sReporterUnenrol:   messageAdmitted,
		commands.CommandK8sReporterUninstall: messageRefused,
		commands.CommandK8sReporterEnrol:     messageRefused,
		commands.CommandReboot:               messageRefused,
		commands.CommandKillProcess:          messageRefused,
	}
	for commandType, message := range want {
		if got := h.answerTo(commandType); got != message {
			t.Errorf("%s answered %q, want %q", commandType, got, message)
		}
	}

	copied = false

	if h.shouldPoll() {
		t.Error("the host kept polling after the copied record was removed")
	}

	if got := h.answerTo(commands.CommandK8sReporterUnenrol); got != messageRefused {
		t.Errorf("unenrol answered %q after the copied record was removed, want it refused", got)
	}
}

// Intent: A copied record beside the host's own changes nothing for the own record: uninstall is admitted.
func TestCommandAdmission_RemoteCommandsOff_WithAnOwnRecordBesideACopiedOne_AdmitsUninstall(t *testing.T) {
	own, copied := true, true
	h := newAdmissionHostFrom(t, false, &own, &copied)

	if got := h.answerTo(commands.CommandK8sReporterUninstall); got != messageAdmitted {
		t.Errorf("uninstall answered %q, want it admitted", got)
	}
}
