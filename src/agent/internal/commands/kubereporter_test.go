// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"strings"
	"testing"
	"time"
)

// fakeKubeReporter records which operation ran and the deadline it ran under.
type fakeKubeReporter struct {
	calls     []string
	deadlines map[string]time.Time
	result    Result
}

func newFakeKubeReporter(result Result) *fakeKubeReporter {
	return &fakeKubeReporter{deadlines: map[string]time.Time{}, result: result}
}

func (f *fakeKubeReporter) record(name string, ctx context.Context) Result {
	f.calls = append(f.calls, name)

	if deadline, ok := ctx.Deadline(); ok {
		f.deadlines[name] = deadline
	}

	return f.result
}

func (f *fakeKubeReporter) Enrol(ctx context.Context) Result     { return f.record("enrol", ctx) }
func (f *fakeKubeReporter) Unenrol(ctx context.Context) Result   { return f.record("unenrol", ctx) }
func (f *fakeKubeReporter) Uninstall(ctx context.Context) Result { return f.record("uninstall", ctx) }

var kubeReporterCommands = []struct {
	cmdType CommandType
	name    string
	timeout time.Duration
}{
	{CommandK8sReporterEnrol, "enrol", 150 * time.Second},
	{CommandK8sReporterUnenrol, "unenrol", 60 * time.Second},
	{CommandK8sReporterUninstall, "uninstall", 150 * time.Second},
}

// Intent: The command type strings are exactly the ones the server and the web client issue.
func TestKubeReporterCommands_TypeNames(t *testing.T) {
	if CommandK8sReporterEnrol != "k8s_reporter_enrol" {
		t.Errorf("enrol = %q", CommandK8sReporterEnrol)
	}
	if CommandK8sReporterUnenrol != "k8s_reporter_unenrol" {
		t.Errorf("unenrol = %q", CommandK8sReporterUnenrol)
	}
	if CommandK8sReporterUninstall != "k8s_reporter_uninstall" {
		t.Errorf("uninstall = %q", CommandK8sReporterUninstall)
	}
}

// Intent: The cluster reporter commands take no parameters. One that carries any is rejected before it reaches
// the reporter, with the message "parameters not allowed", and acknowledged as rejected rather than failed.
func TestExecutors_RejectParameters(t *testing.T) {
	for _, tc := range kubeReporterCommands {
		t.Run(string(tc.cmdType), func(t *testing.T) {
			reporter := newFakeKubeReporter(Result{Success: true, Message: "ok"})
			handler := NewHandler(HandlerDeps{KubeReporter: reporter})

			result := handler.Execute(context.Background(), Command{Type: tc.cmdType, Params: map[string]string{"cluster": "other"}})

			if result.Success {
				t.Fatal("a command with parameters must not succeed")
			}

			if result.Rejected == false {
				t.Error("a command with parameters must be marked rejected")
			}

			if result.Message != "parameters not allowed" {
				t.Errorf("message = %q, want %q", result.Message, "parameters not allowed")
			}

			if len(reporter.calls) != 0 {
				t.Errorf("the reporter ran for a command with parameters: %v", reporter.calls)
			}
		})
	}
}

// Intent: No parameters, whether the map is nil or empty, runs the matching reporter operation and returns its
// result unchanged.
func TestExecutors_RunMatchingOperation(t *testing.T) {
	for _, tc := range kubeReporterCommands {
		for _, params := range []map[string]string{nil, {}} {
			t.Run(string(tc.cmdType), func(t *testing.T) {
				reporter := newFakeKubeReporter(Result{Success: true, Message: "done: " + tc.name})
				handler := NewHandler(HandlerDeps{KubeReporter: reporter})

				result := handler.Execute(context.Background(), Command{Type: tc.cmdType, Params: params})

				if (result.Success == false) || (result.Message != "done: "+tc.name) || result.Rejected {
					t.Errorf("result = %+v", result)
				}

				if len(reporter.calls) != 1 || reporter.calls[0] != tc.name {
					t.Errorf("calls = %v, want [%s]", reporter.calls, tc.name)
				}
			})
		}
	}
}

// Intent: A failure keeps its reason code in the message, so it reaches the server, and is not marked rejected.
func TestExecutors_FailureKeepsReasonCode(t *testing.T) {
	reporter := newFakeKubeReporter(Result{Success: false, Message: "name_conflict: ClusterRole vord-reporter exists without the label"})
	handler := NewHandler(HandlerDeps{KubeReporter: reporter})

	result := handler.Execute(context.Background(), Command{Type: CommandK8sReporterEnrol})

	if result.Success || result.Rejected {
		t.Errorf("result = %+v", result)
	}

	if strings.HasPrefix(result.Message, "name_conflict") == false {
		t.Errorf("message = %q", result.Message)
	}
}

// Intent: Enrol and uninstall run under 150 seconds and unenrol under 60.
func TestExecutors_RunUnderTheirDeadline(t *testing.T) {
	for _, tc := range kubeReporterCommands {
		t.Run(tc.name, func(t *testing.T) {
			reporter := newFakeKubeReporter(Result{Success: true})
			handler := NewHandler(HandlerDeps{KubeReporter: reporter})

			earliest := time.Now()
			handler.Execute(context.Background(), Command{Type: tc.cmdType})
			latest := time.Now()

			deadline, ok := reporter.deadlines[tc.name]
			if ok == false {
				t.Fatal("the reporter ran without a deadline")
			}

			if deadline.Before(earliest.Add(tc.timeout)) || deadline.After(latest.Add(tc.timeout)) {
				t.Errorf("deadline %v is not %v after the call (between %v and %v)", deadline, tc.timeout, earliest, latest)
			}
		})
	}
}

// Intent: A parent deadline shorter than the command's own is kept.
func TestExecutors_KeepShorterParentDeadline(t *testing.T) {
	reporter := newFakeKubeReporter(Result{Success: true})
	handler := NewHandler(HandlerDeps{KubeReporter: reporter})

	parent, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	parentDeadline, _ := parent.Deadline()

	handler.Execute(parent, Command{Type: CommandK8sReporterEnrol})

	if got := reporter.deadlines["enrol"]; got.After(parentDeadline) {
		t.Errorf("deadline %v is later than the parent's %v", got, parentDeadline)
	}
}

// Intent: An agent built without a reporter fails the commands with a clear reason instead of panicking.
func TestExecutors_NoReporter_FailsAsUnavailable(t *testing.T) {
	handler := NewHandler(HandlerDeps{})

	for _, tc := range kubeReporterCommands {
		result := handler.Execute(context.Background(), Command{Type: tc.cmdType})

		if result.Success || (result.Message != "reporter_unavailable") {
			t.Errorf("%s: result = %+v", tc.cmdType, result)
		}
	}
}

// signedReporterCommand builds the payload the web client signs for a reporter command, with the given params
// object (empty for none).
func signedReporterCommand(cmdType CommandType, paramsJSON string) string {
	signed := strings.Replace(webSignedCheckUpdates, `"command_type":"check_updates"`, `"command_type":"`+string(cmdType)+`"`, 1)

	if paramsJSON != "" {
		signed = strings.Replace(signed, `"tenant_id"`, `"params":`+paramsJSON+`,"tenant_id"`, 1)
	}

	return signed
}

// Intent: Through the processor, a signed reporter command with no parameters executes once and is
// acknowledged as completed with the reporter's message; none of the three is destructive, so there is no
// initiated acknowledgement before it.
func TestProcess_ReporterCommand_RunsAndAcknowledgesCompleted(t *testing.T) {
	for _, tc := range kubeReporterCommands {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)
			reporter := newFakeKubeReporter(Result{Success: true, Message: "done"})
			f.processor.handler = NewHandler(HandlerDeps{KubeReporter: reporter})

			cmd := f.deliver(signedReporterCommand(tc.cmdType, ""))
			cmd.Type = string(tc.cmdType)
			f.process(cmd)

			if len(f.ack.calls) != 1 {
				t.Fatalf("expected 1 ack, got %d: %+v", len(f.ack.calls), f.ack.calls)
			}

			if (f.ack.calls[0].ResultType != ResultTypeCompleted) || (f.ack.calls[0].Message != "done") {
				t.Errorf("ack = %+v", f.ack.calls[0])
			}

			if len(reporter.calls) != 1 || reporter.calls[0] != tc.name {
				t.Errorf("reporter calls = %v", reporter.calls)
			}
		})
	}
}

// Intent: Through the processor, a signed reporter command that carries parameters is acknowledged as REJECTED
// with "parameters not allowed", and the reporter never runs.
func TestProcess_ReporterCommandWithParameters_AcknowledgedRejected(t *testing.T) {
	for _, tc := range kubeReporterCommands {
		t.Run(tc.name, func(t *testing.T) {
			f := newSignedFixture(t)
			reporter := newFakeKubeReporter(Result{Success: true})
			f.processor.handler = NewHandler(HandlerDeps{KubeReporter: reporter})

			cmd := f.deliver(signedReporterCommand(tc.cmdType, `{"a":"b"}`))
			cmd.Type = string(tc.cmdType)
			cmd.Params = map[string]string{"a": "b"}
			f.process(cmd)

			if len(f.ack.calls) != 1 {
				t.Fatalf("expected 1 ack, got %d: %+v", len(f.ack.calls), f.ack.calls)
			}

			ack := f.ack.calls[0]
			if (ack.ResultType != ResultTypeRejected) || ack.Success || (ack.Message != "parameters not allowed") {
				t.Errorf("ack = %+v, want a rejection with %q", ack, "parameters not allowed")
			}

			if len(reporter.calls) != 0 {
				t.Errorf("the reporter ran: %v", reporter.calls)
			}
		})
	}
}

// Intent: A reporter failure is acknowledged as failed, with the reason code as its message.
func TestProcess_ReporterCommandFailure_AcknowledgedFailedWithReason(t *testing.T) {
	f := newSignedFixture(t)
	reporter := newFakeKubeReporter(Result{Success: false, Message: "api_unreachable: cannot reach https://10.0.0.1:6443"})
	f.processor.handler = NewHandler(HandlerDeps{KubeReporter: reporter})

	cmd := f.deliver(signedReporterCommand(CommandK8sReporterUninstall, ""))
	cmd.Type = string(CommandK8sReporterUninstall)
	f.process(cmd)

	if len(f.ack.calls) != 1 {
		t.Fatalf("expected 1 ack, got %d", len(f.ack.calls))
	}

	ack := f.ack.calls[0]
	if (ack.ResultType != ResultTypeFailed) || ack.Success || strings.HasPrefix(ack.Message, "api_unreachable") == false {
		t.Errorf("ack = %+v", ack)
	}
}
