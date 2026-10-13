// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"crypto/ed25519"
	"testing"
	"time"
)

const (
	cleanupTestTenantID  = int32(100)
	cleanupTestMachineID = int64(200)
	cleanupTestKeyID     = int32(1)

	// cleanupTestDestructiveDelay is long enough that a test which reached the delay would hang, so the
	// destructive cases cancel the context first.
	cleanupTestDestructiveDelay = 10 * time.Minute

	remoteCommandsDisabledMessage = "remote commands are disabled on this host"
)

// countingExecutor records how many times it ran.
type countingExecutor struct {
	calls  int
	result Result
}

func (c *countingExecutor) Execute(_ context.Context, _ map[string]string) Result {
	c.calls++

	return c.result
}

// cleanupOnlyFixture is a processor with remote commands off, together with everything a test inspects.
type cleanupOnlyFixture struct {
	t         *testing.T
	pub       ed25519.PublicKey
	priv      ed25519.PrivateKey
	store     *mockNonceStore
	ack       *mockAcknowledger
	executor  *countingExecutor
	processor *Processor

	keyLookups   int
	nonceChecks  int
	recordChecks int
	recordHeld   bool

	// copiedHeld is whether the host holds a record written on another host, and copiedChecks how often that was
	// asked.
	copiedHeld   bool
	copiedChecks int
}

// newCleanupOnlyFixture builds a processor whose executors are all one counting executor. The record check reads
// the fixture's recordHeld field, so a test can change it between commands.
func newCleanupOnlyFixture(t *testing.T, allowAll bool, recordHeld bool) *cleanupOnlyFixture {
	t.Helper()

	pub, priv := testKeyPair(t)
	f := &cleanupOnlyFixture{
		t:          t,
		pub:        pub,
		priv:       priv,
		ack:        &mockAcknowledger{},
		executor:   &countingExecutor{result: Result{Success: true, Message: "done"}},
		recordHeld: recordHeld,
	}

	f.store = &mockNonceStore{
		getKeyFn: func(int32) (*SigningKey, error) {
			f.keyLookups++

			return &SigningKey{KeyID: cleanupTestKeyID, UserID: 42, PublicKey: pub}, nil
		},
		isUsedFn: func(string) (bool, error) {
			f.nonceChecks++

			return false, nil
		},
	}

	handler := &Handler{executors: make(map[CommandType]Executor)}
	for commandType := range validCommandTypes {
		handler.executors[commandType] = f.executor
	}

	f.processor = newTestProcessorWithOptions(handler, f.store, f.ack, cleanupTestDestructiveDelay, ProcessorOptions{
		AllowAll: allowAll,
		LocalRecordPresent: func() bool {
			f.recordChecks++

			return f.recordHeld
		},
		CopiedRecordPresent: func() bool {
			f.copiedChecks++

			return f.copiedHeld
		},
	})

	return f
}

// process delivers a correctly signed command of the given type. The context is already cancelled so a command
// that wrongly reached the destructive delay returns at once instead of waiting out its ten minutes.
func (f *cleanupOnlyFixture) process(commandType CommandType) PendingCommand {
	f.t.Helper()

	cmd := validPendingCommand(f.t, commandType, cleanupTestTenantID, cleanupTestMachineID, f.priv, f.pub, cleanupTestKeyID)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	f.processor.Process(ctx, cmd, cleanupTestTenantID, cleanupTestMachineID)

	return cmd
}

// requireRefusedBeforeAnyWork fails unless the last command was acknowledged once, as rejected with the
// remote-commands-disabled text, and nothing past the type check ran.
func (f *cleanupOnlyFixture) requireRefusedBeforeAnyWork() {
	f.t.Helper()

	if len(f.ack.calls) != 1 {
		f.t.Fatalf("got %d acks %+v, want exactly one rejection", len(f.ack.calls), f.ack.calls)
	}

	got := f.ack.calls[0]
	if (got.ResultType != ResultTypeRejected) || got.Success || (got.Message != remoteCommandsDisabledMessage) {
		f.t.Errorf("ack = %+v, want a failed REJECTED ack with %q", got, remoteCommandsDisabledMessage)
	}

	if f.executor.calls != 0 {
		f.t.Errorf("the executor ran %d times, want 0", f.executor.calls)
	}

	if len(f.store.recordedNonces) != 0 {
		f.t.Errorf("nonces %v were recorded, want none", f.store.recordedNonces)
	}

	if f.nonceChecks != 0 {
		f.t.Errorf("the nonce store was consulted %d times, want 0", f.nonceChecks)
	}

	if f.keyLookups != 0 {
		f.t.Errorf("the signing key was looked up %d times, want 0 (no signature work before the refusal)", f.keyLookups)
	}
}

// requireProcessed fails unless the last command ran its executor once, recorded its nonce and was acknowledged
// as completed.
func (f *cleanupOnlyFixture) requireProcessed() {
	f.t.Helper()

	if f.executor.calls != 1 {
		f.t.Errorf("the executor ran %d times, want 1", f.executor.calls)
	}

	if len(f.store.recordedNonces) != 1 {
		f.t.Errorf("recorded nonces %v, want exactly one", f.store.recordedNonces)
	}

	if len(f.ack.calls) != 1 {
		f.t.Fatalf("got %d acks %+v, want exactly one", len(f.ack.calls), f.ack.calls)
	}

	if got := f.ack.calls[0]; (got.ResultType != ResultTypeCompleted) || (got.Success == false) {
		f.t.Errorf("ack = %+v, want a successful completed ack", got)
	}
}

// Intent: With remote commands off, every type other than the two cleanup commands is refused, even on a host
// that holds a reporter record, and the refusal comes before the signature, the nonce, the destructive delay
// and the executor.
func TestProcess_RemoteCommandsOff_RefusesEveryTypeButCleanup(t *testing.T) {
	refused := []CommandType{
		CommandReboot,
		CommandKillProcess,
		CommandKillSession,
		CommandCheckUpdate,
		CommandInstallUpdate,
		CommandK8sReporterEnrol,
	}

	for _, commandType := range refused {
		t.Run(string(commandType), func(t *testing.T) {
			f := newCleanupOnlyFixture(t, false, true)

			f.process(commandType)

			f.requireRefusedBeforeAnyWork()
		})
	}
}

// Intent: A destructive command refused for the flag is never acknowledged as initiated, so the host does not
// announce a reboot it will not perform.
func TestProcess_RemoteCommandsOff_RebootIsNeverInitiated(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, true)

	f.process(CommandReboot)

	for _, call := range f.ack.calls {
		if call.ResultType == ResultTypeInitiated {
			t.Fatalf("a refused reboot was acknowledged as initiated: %+v", call)
		}
	}

	f.requireRefusedBeforeAnyWork()
}

// Intent: On a host that holds a reporter record, unenrol and uninstall run exactly as they do with the flag on.
func TestProcess_RemoteCommandsOff_WithRecord_RunsCleanupCommands(t *testing.T) {
	for _, commandType := range []CommandType{CommandK8sReporterUnenrol, CommandK8sReporterUninstall} {
		t.Run(string(commandType), func(t *testing.T) {
			f := newCleanupOnlyFixture(t, false, true)

			f.process(commandType)

			f.requireProcessed()
		})
	}
}

// Intent: A host that never enrolled keeps its opt-out for all three reporter commands, so an uninstall can
// never run with the admin credential of a control plane that has not agreed to remote commands.
func TestProcess_RemoteCommandsOff_WithoutRecord_RefusesAllReporterCommands(t *testing.T) {
	reporterCommands := []CommandType{CommandK8sReporterEnrol, CommandK8sReporterUnenrol, CommandK8sReporterUninstall}

	for _, commandType := range reporterCommands {
		t.Run(string(commandType), func(t *testing.T) {
			f := newCleanupOnlyFixture(t, false, false)

			f.process(commandType)

			f.requireRefusedBeforeAnyWork()
		})
	}
}

// Intent: A processor built with remote commands off and no way to check the record fails closed.
func TestProcess_RemoteCommandsOff_WithoutARecordCheck_RefusesCleanup(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, true)
	f.processor = newTestProcessorWithOptions(f.processor.handler, f.store, f.ack, cleanupTestDestructiveDelay, ProcessorOptions{})

	f.process(CommandK8sReporterUninstall)

	f.requireRefusedBeforeAnyWork()
}

// Intent: The record is checked for each command, so a host whose last record was removed by an earlier
// cleanup command refuses the next one instead of trusting a stale answer.
func TestProcess_RemoteCommandsOff_ChecksTheRecordForEveryCommand(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, true)

	f.process(CommandK8sReporterUnenrol)
	f.requireProcessed()

	f.recordHeld = false
	f.ack.calls = nil
	f.store.recordedNonces = nil
	f.executor.calls = 0
	f.keyLookups = 0
	f.nonceChecks = 0

	f.process(CommandK8sReporterUninstall)
	f.requireRefusedBeforeAnyWork()

	if f.recordChecks != 2 {
		t.Errorf("the record was checked %d times, want once per cleanup command", f.recordChecks)
	}
}

// Intent: The refusal comes right after the type check. A command that would otherwise fail on tenant, machine,
// signing key or signature is still answered with the flag text, so nothing about the host's key store or
// ownership is revealed or consulted while remote commands are off.
func TestProcess_RemoteCommandsOff_RefusesBeforeOwnershipKeyAndSignature(t *testing.T) {
	cases := []struct {
		name   string
		mutate func(cmd *PendingCommand)
	}{
		{name: "tenant mismatch", mutate: func(cmd *PendingCommand) { cmd.TenantID = cleanupTestTenantID + 1 }},
		{name: "machine mismatch", mutate: func(cmd *PendingCommand) { cmd.MachineID = cleanupTestMachineID + 1 }},
		{name: "broken signature", mutate: func(cmd *PendingCommand) { cmd.Signature = []byte("not a signature") }},
		{name: "empty payload", mutate: func(cmd *PendingCommand) { cmd.CanonicalPayload = "" }},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newCleanupOnlyFixture(t, false, false)

			cmd := validPendingCommand(t, CommandReboot, cleanupTestTenantID, cleanupTestMachineID, f.priv, f.pub, cleanupTestKeyID)
			tc.mutate(&cmd)

			f.processor.Process(context.Background(), cmd, cleanupTestTenantID, cleanupTestMachineID)

			f.requireRefusedBeforeAnyWork()
		})
	}
}

// Intent: A type the agent does not know is still reported as unknown with the flag off; the flag only decides
// among types the agent does recognise.
func TestProcess_RemoteCommandsOff_UnknownTypeIsStillUnknown(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, true)

	cmd := validPendingCommand(t, CommandReboot, cleanupTestTenantID, cleanupTestMachineID, f.priv, f.pub, cleanupTestKeyID)
	cmd.Type = "format_disk"

	f.processor.Process(context.Background(), cmd, cleanupTestTenantID, cleanupTestMachineID)

	if len(f.ack.calls) != 1 {
		t.Fatalf("got %d acks, want 1", len(f.ack.calls))
	}

	if got := f.ack.calls[0]; (got.ResultType != ResultTypeRejected) || (got.Message != "unknown command type") {
		t.Errorf("ack = %+v, want REJECTED \"unknown command type\"", got)
	}

	if f.recordChecks != 0 {
		t.Errorf("the record was checked %d times for an unknown type, want 0", f.recordChecks)
	}
}

// Intent: The refusal reads the delivered type, so a reboot relabelled as unenrol to get past it must still be
// stopped by the binding of the signed payload to the delivered command, and never run.
func TestProcess_RemoteCommandsOff_RelabelledRebootIsStillRejected(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, true)

	cmd := validPendingCommand(t, CommandReboot, cleanupTestTenantID, cleanupTestMachineID, f.priv, f.pub, cleanupTestKeyID)
	cmd.Type = string(CommandK8sReporterUnenrol)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	f.processor.Process(ctx, cmd, cleanupTestTenantID, cleanupTestMachineID)

	if f.executor.calls != 0 {
		t.Errorf("the executor ran %d times, want 0", f.executor.calls)
	}

	if len(f.ack.calls) != 1 {
		t.Fatalf("got %d acks %+v, want exactly one rejection", len(f.ack.calls), f.ack.calls)
	}

	if got := f.ack.calls[0]; (got.ResultType != ResultTypeRejected) || (got.Message != "canonical payload mismatch") {
		t.Errorf("ack = %+v, want REJECTED \"canonical payload mismatch\"", got)
	}
}

// Intent: With remote commands on, the processor never consults the record and accepts every type, enrol
// included, on a host that holds none.
func TestProcess_RemoteCommandsOn_IgnoresTheRecord(t *testing.T) {
	for _, commandType := range []CommandType{CommandK8sReporterEnrol, CommandK8sReporterUnenrol, CommandK8sReporterUninstall, CommandCheckUpdate} {
		t.Run(string(commandType), func(t *testing.T) {
			f := newCleanupOnlyFixture(t, true, false)

			f.process(commandType)

			f.requireProcessed()

			if f.recordChecks != 0 {
				t.Errorf("the record was checked %d times, want 0 when every command is allowed", f.recordChecks)
			}
		})
	}
}

// Intent: A host with remote commands off whose only record was written on another host takes the unenrol, which
// for such a record removes local files and nothing in the cluster. Uninstall, enrol and every other command stay
// refused before any work: the host never enrolled, so its opt-out holds.
func TestProcess_RemoteCommandsOff_WithOnlyACopiedRecord_RunsUnenrolAndRefusesTheRest(t *testing.T) {
	f := newCleanupOnlyFixture(t, false, false)
	f.copiedHeld = true

	f.process(CommandK8sReporterUnenrol)
	f.requireProcessed()

	for _, commandType := range []CommandType{CommandK8sReporterUninstall, CommandK8sReporterEnrol, CommandReboot} {
		t.Run(string(commandType), func(t *testing.T) {
			g := newCleanupOnlyFixture(t, false, false)
			g.copiedHeld = true

			g.process(commandType)

			g.requireRefusedBeforeAnyWork()
		})
	}
}

// Intent: An own record admits both cleanup commands without asking about copied ones, and the copied record is
// asked about only when the own record has not already settled the question.
func TestProcess_RemoteCommandsOff_CopiedRecordIsConsultedOnlyWhenNeeded(t *testing.T) {
	own := newCleanupOnlyFixture(t, false, true)
	own.copiedHeld = true

	own.process(CommandK8sReporterUninstall)
	own.requireProcessed()

	if own.copiedChecks != 0 {
		t.Errorf("the copied record was checked %d times although an own record admitted the command", own.copiedChecks)
	}

	flagOn := newCleanupOnlyFixture(t, true, false)

	flagOn.process(CommandK8sReporterUninstall)
	flagOn.requireProcessed()

	if (flagOn.recordChecks != 0) || (flagOn.copiedChecks != 0) {
		t.Errorf("records were checked (%d own, %d copied) although remote commands are on", flagOn.recordChecks, flagOn.copiedChecks)
	}
}

// Intent: A host with no record of either kind refuses unenrol, and a processor without a copied-record check
// fails closed for it.
func TestProcess_RemoteCommandsOff_WithNoCopiedRecordOrNoCheck_RefusesUnenrol(t *testing.T) {
	none := newCleanupOnlyFixture(t, false, false)

	none.process(CommandK8sReporterUnenrol)
	none.requireRefusedBeforeAnyWork()

	if none.copiedChecks != 1 {
		t.Errorf("the copied record was checked %d times, want once", none.copiedChecks)
	}

	unchecked := newCleanupOnlyFixture(t, false, false)
	unchecked.copiedHeld = true
	unchecked.processor = newTestProcessorWithOptions(unchecked.processor.handler, unchecked.store, unchecked.ack, cleanupTestDestructiveDelay, ProcessorOptions{
		LocalRecordPresent: func() bool { return false },
	})

	unchecked.process(CommandK8sReporterUnenrol)
	unchecked.requireRefusedBeforeAnyWork()
}
