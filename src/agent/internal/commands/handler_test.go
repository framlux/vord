// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"fmt"
	"os"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"time"
)

// --- Mock implementations ---

// mockSignaler records calls and returns configurable errors.
type mockSignaler struct {
	calls    []mockSignalCall
	signalFn func(pid int, sig syscall.Signal) error
}

type mockSignalCall struct {
	PID int
	Sig syscall.Signal
}

func (m *mockSignaler) Signal(pid int, sig syscall.Signal) error {
	m.calls = append(m.calls, mockSignalCall{PID: pid, Sig: sig})
	if m.signalFn != nil {
		return m.signalFn(pid, sig)
	}

	return nil
}

// mockRunner records calls and returns configurable output.
type mockRunner struct {
	calls []mockRunCall
	runFn func(ctx context.Context, name string, args ...string) ([]byte, error)
}

type mockRunCall struct {
	Name string
	Args []string
}

func (m *mockRunner) Run(ctx context.Context, name string, args ...string) ([]byte, error) {
	m.calls = append(m.calls, mockRunCall{Name: name, Args: args})
	if m.runFn != nil {
		return m.runFn(ctx, name, args...)
	}

	return nil, nil
}

// mockLooker returns configurable results for LookPath.
type mockLooker struct {
	available map[string]bool
}

func (m *mockLooker) LookPath(file string) (string, error) {
	if m.available[file] {
		return "/usr/bin/" + file, nil
	}

	return "", fmt.Errorf("executable file not found: %s", file)
}

// --- Handler tests ---

// Intent: Handler has executors for all 5 command types.
func TestNewHandler_AllExecutorsRegistered(t *testing.T) {
	h := NewHandler()

	expectedTypes := []CommandType{
		CommandReboot,
		CommandKillProcess,
		CommandKillSession,
		CommandCheckUpdate,
		CommandInstallUpdate,
	}

	for _, ct := range expectedTypes {
		if _, ok := h.executors[ct]; ok == false {
			t.Errorf("missing executor for command type %q", ct)
		}
	}

	if len(h.executors) != len(expectedTypes) {
		t.Errorf("expected %d executors, got %d", len(expectedTypes), len(h.executors))
	}
}

// Intent: Unknown command type returns error result.
func TestExecute_UnknownCommand(t *testing.T) {
	h := NewHandler()

	result := h.Execute(context.Background(), Command{
		Type:   "nonexistent_command",
		Params: map[string]string{},
	})

	if result.Success {
		t.Error("expected Success=false for unknown command")
	}
	if result.Error == nil {
		t.Error("expected non-nil error for unknown command")
	}
	if strings.Contains(result.Error.Error(), "unknown command type") == false {
		t.Errorf("expected 'unknown command type' in error, got %q", result.Error.Error())
	}
}

// Intent: Each command type routes to its registered executor (verified by returning known error for missing params).
func TestExecute_DispatchesToCorrectExecutor(t *testing.T) {
	h := NewHandler()

	tests := []struct {
		cmdType     CommandType
		expectInErr string
	}{
		{CommandKillProcess, "pid parameter required"},
		{CommandKillSession, "session_id parameter required"},
	}

	for _, tt := range tests {
		t.Run(string(tt.cmdType), func(t *testing.T) {
			result := h.Execute(context.Background(), Command{
				Type:   tt.cmdType,
				Params: map[string]string{},
			})

			if result.Success {
				t.Errorf("expected failure for %s with empty params", tt.cmdType)
			}
			if result.Error == nil {
				t.Fatalf("expected error for %s with empty params", tt.cmdType)
			}
			if strings.Contains(result.Error.Error(), tt.expectInErr) == false {
				t.Errorf("expected %q in error, got %q", tt.expectInErr, result.Error.Error())
			}
		})
	}
}

// --- KillProcess validation tests ---

// Intent: Missing "pid" param produces validation error.
func TestKillProcess_MissingPID(t *testing.T) {
	e := &KillProcessExecutor{}
	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for missing pid")
	}
	if result.Error == nil || (strings.Contains(result.Error.Error(), "pid parameter required") == false) {
		t.Errorf("expected 'pid parameter required' error, got %v", result.Error)
	}
}

// Intent: Non-numeric PID produces validation error.
func TestKillProcess_NonNumericPID(t *testing.T) {
	e := &KillProcessExecutor{}
	result := e.Execute(context.Background(), map[string]string{"pid": "abc"})

	if result.Success {
		t.Error("expected Success=false for non-numeric pid")
	}
	if result.Error == nil || (strings.Contains(result.Error.Error(), "invalid pid") == false) {
		t.Errorf("expected 'invalid pid' error, got %v", result.Error)
	}
}

// Intent: PID <= 100 must be rejected (protected range) to prevent killing critical system processes.
func TestKillProcess_ProtectedPID(t *testing.T) {
	e := &KillProcessExecutor{}

	protectedPIDs := []string{"0", "1", "50", "100"}
	for _, pid := range protectedPIDs {
		t.Run("pid_"+pid, func(t *testing.T) {
			result := e.Execute(context.Background(), map[string]string{"pid": pid})

			if result.Success {
				t.Errorf("expected Success=false for protected pid %s", pid)
			}
			if result.Error == nil || (strings.Contains(result.Error.Error(), "protected range") == false) {
				t.Errorf("expected 'protected range' error for pid %s, got %v", pid, result.Error)
			}
		})
	}
}

// Intent: PID == os.Getpid() must be rejected (self-protection).
func TestKillProcess_SelfPID(t *testing.T) {
	e := &KillProcessExecutor{}
	selfPID := os.Getpid()

	// Self PID is likely >100, so it won't hit the protected range check first.
	if selfPID <= 100 {
		t.Skip("self PID is in protected range, cannot test self-protection separately")
	}

	result := e.Execute(context.Background(), map[string]string{
		"pid": strconv.Itoa(selfPID),
	})

	if result.Success {
		t.Error("expected Success=false for self PID")
	}
	if result.Error == nil || (strings.Contains(result.Error.Error(), "refusing to kill agent process") == false) {
		t.Errorf("expected 'refusing to kill agent process' error, got %v", result.Error)
	}
}

// --- KillProcess execution tests ---

// Intent: Force kill sends SIGKILL and returns success when signaler succeeds.
func TestKillProcess_ForceKill_Success(t *testing.T) {
	sig := &mockSignaler{}
	e := &KillProcessExecutor{signaler: sig}

	result := e.Execute(context.Background(), map[string]string{
		"pid":   "12345",
		"force": "true",
	})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "SIGKILL") == false {
		t.Errorf("expected 'SIGKILL' in message, got %q", result.Message)
	}
	if len(sig.calls) != 1 {
		t.Fatalf("expected 1 signal call, got %d", len(sig.calls))
	}
	if sig.calls[0].Sig != syscall.SIGKILL {
		t.Errorf("expected SIGKILL, got %v", sig.calls[0].Sig)
	}
}

// Intent: Force kill returns error when signaler fails (e.g., ESRCH).
func TestKillProcess_ForceKill_SignalError(t *testing.T) {
	sig := &mockSignaler{
		signalFn: func(pid int, s syscall.Signal) error {
			return syscall.ESRCH
		},
	}
	e := &KillProcessExecutor{signaler: sig}

	result := e.Execute(context.Background(), map[string]string{
		"pid":   "12345",
		"force": "true",
	})

	if result.Success {
		t.Error("expected Success=false for ESRCH")
	}
	if result.Error == nil {
		t.Error("expected non-nil error")
	}
}

// Intent: SIGTERM succeeds, process exits (signal-0 returns error) -> success with "SIGTERM" message.
func TestKillProcess_SIGTERM_ProcessExits(t *testing.T) {
	callCount := 0
	sig := &mockSignaler{
		signalFn: func(pid int, s syscall.Signal) error {
			callCount++
			if s == syscall.SIGTERM {
				return nil
			}
			// Signal-0 check: process already exited.
			if s == 0 {
				return syscall.ESRCH
			}

			return nil
		},
	}
	e := &KillProcessExecutor{signaler: sig}

	result := e.Execute(context.Background(), map[string]string{"pid": "12345"})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "SIGTERM") == false {
		t.Errorf("expected 'SIGTERM' in message, got %q", result.Message)
	}
}

// Intent: SIGTERM succeeds, process stays alive, SIGKILL succeeds -> "SIGTERM+SIGKILL" message.
func TestKillProcess_SIGTERM_EscalateToSIGKILL(t *testing.T) {
	sig := &mockSignaler{
		signalFn: func(pid int, s syscall.Signal) error {
			// Signal-0 always succeeds (process alive), SIGTERM/SIGKILL succeed.
			return nil
		},
	}
	e := &KillProcessExecutor{signaler: sig}

	result := e.Execute(context.Background(), map[string]string{"pid": "12345"})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "SIGTERM+SIGKILL") == false {
		t.Errorf("expected 'SIGTERM+SIGKILL' in message, got %q", result.Message)
	}
}

// Intent: SIGTERM returns EPERM -> error result.
func TestKillProcess_SIGTERM_Fails(t *testing.T) {
	sig := &mockSignaler{
		signalFn: func(pid int, s syscall.Signal) error {
			if s == syscall.SIGTERM {
				return syscall.EPERM
			}

			return nil
		},
	}
	e := &KillProcessExecutor{signaler: sig}

	result := e.Execute(context.Background(), map[string]string{"pid": "12345"})

	if result.Success {
		t.Error("expected Success=false for EPERM on SIGTERM")
	}
}

// --- waitForExit tests ---

// Intent: Process exits immediately (signal-0 returns error on first check).
func TestWaitForExit_ProcessExitsImmediately(t *testing.T) {
	sig := &mockSignaler{
		signalFn: func(pid int, s syscall.Signal) error {
			return syscall.ESRCH
		},
	}

	exited := waitForExit(context.Background(), 12345, 1*time.Second, sig)
	if exited == false {
		t.Error("expected process to be reported as exited")
	}
}

// Intent: Cancelled context returns false.
func TestWaitForExit_ContextCancelled(t *testing.T) {
	sig := &mockSignaler{} // signal-0 always succeeds (process alive)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	exited := waitForExit(ctx, 12345, 1*time.Second, sig)
	if exited {
		t.Error("expected false for cancelled context")
	}
}

// --- KillSession execution tests ---

// Intent: loginctl succeeds -> success result.
func TestKillSession_Success(t *testing.T) {
	runner := &mockRunner{}
	e := &KillSessionExecutor{runner: runner}

	result := e.Execute(context.Background(), map[string]string{"session_id": "abc123"})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "abc123") == false {
		t.Errorf("expected session ID in message, got %q", result.Message)
	}
	if len(runner.calls) != 1 {
		t.Fatalf("expected 1 run call, got %d", len(runner.calls))
	}
	if runner.calls[0].Name != "loginctl" {
		t.Errorf("expected loginctl, got %q", runner.calls[0].Name)
	}
}

// Intent: loginctl fails -> error result with output.
func TestKillSession_LoginctlFails(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("session not found"), fmt.Errorf("exit status 1")
		},
	}
	e := &KillSessionExecutor{runner: runner}

	result := e.Execute(context.Background(), map[string]string{"session_id": "abc123"})

	if result.Success {
		t.Error("expected Success=false for loginctl failure")
	}
	if result.Error == nil {
		t.Error("expected non-nil error")
	}
}

// --- KillSession validation tests ---

// Intent: Missing "session_id" produces validation error.
func TestKillSession_MissingSessionID(t *testing.T) {
	e := &KillSessionExecutor{}
	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for missing session_id")
	}
	if result.Error == nil || (strings.Contains(result.Error.Error(), "session_id parameter required") == false) {
		t.Errorf("expected 'session_id parameter required' error, got %v", result.Error)
	}
}

// Intent: Session ID with shell metacharacters must be rejected to prevent injection.
func TestKillSession_InvalidSessionID(t *testing.T) {
	e := &KillSessionExecutor{}

	badIDs := []string{
		"ses;rm -rf /",
		"ses$(whoami)",
		"ses`id`",
		"ses|cat /etc/passwd",
		"ses&bg",
		"ses id",
		"../etc/passwd",
	}

	for _, id := range badIDs {
		t.Run(id, func(t *testing.T) {
			result := e.Execute(context.Background(), map[string]string{"session_id": id})

			if result.Success {
				t.Errorf("expected Success=false for invalid session_id %q", id)
			}
			if result.Error == nil || (strings.Contains(result.Error.Error(), "invalid session_id format") == false) {
				t.Errorf("expected 'invalid session_id format' for %q, got %v", id, result.Error)
			}
		})
	}
}

// Intent: Alphanumeric session IDs pass validation.
func TestKillSession_ValidSessionID(t *testing.T) {
	runner := &mockRunner{}
	validIDs := []string{"abc123", "session-42", "sess_01"}
	for _, id := range validIDs {
		t.Run(id, func(t *testing.T) {
			e := &KillSessionExecutor{runner: runner}
			result := e.Execute(context.Background(), map[string]string{"session_id": id})

			if result.Success == false {
				t.Errorf("valid session_id %q should succeed, got error: %v", id, result.Error)
			}
		})
	}
}

// --- Reboot pure function tests ---

// Intent: Control characters are stripped from reason.
func TestSanitizeReason_ControlCharsStripped(t *testing.T) {
	result := sanitizeReason("test\n\t\x00reason")
	if strings.ContainsAny(result, "\n\t\x00") {
		t.Errorf("expected control chars stripped, got %q", result)
	}
	if result != "testreason" {
		t.Errorf("expected %q, got %q", "testreason", result)
	}
}

// Intent: Flag-like reason (starting with "-") is replaced with default.
func TestSanitizeReason_FlagLikeRejected(t *testing.T) {
	result := sanitizeReason("-rf /")
	if result != defaultRebootReason {
		t.Errorf("expected default reason, got %q", result)
	}
}

// Intent: Long reason is truncated to 200 characters.
func TestSanitizeReason_TruncatedAt200(t *testing.T) {
	long := strings.Repeat("a", 300)
	result := sanitizeReason(long)
	if len(result) != 200 {
		t.Errorf("expected length 200, got %d", len(result))
	}
}

// Intent: Normal reason passes through unchanged.
func TestSanitizeReason_NormalPassthrough(t *testing.T) {
	result := sanitizeReason("Scheduled maintenance")
	if result != "Scheduled maintenance" {
		t.Errorf("expected %q, got %q", "Scheduled maintenance", result)
	}
}

// Intent: Empty reason returns default.
func TestSanitizeReason_EmptyReturnsDefault(t *testing.T) {
	result := sanitizeReason("")
	if result != defaultRebootReason {
		t.Errorf("expected default reason, got %q", result)
	}
}

// Intent: Missing delay_minutes param returns 0.
func TestParseDelayMinutes_Missing(t *testing.T) {
	result := parseDelayMinutes(map[string]string{})
	if result != 0 {
		t.Errorf("expected 0, got %d", result)
	}
}

// Intent: Valid integer is returned as-is.
func TestParseDelayMinutes_ValidInt(t *testing.T) {
	result := parseDelayMinutes(map[string]string{"delay_minutes": "30"})
	if result != 30 {
		t.Errorf("expected 30, got %d", result)
	}
}

// Intent: Negative value returns 0.
func TestParseDelayMinutes_Negative(t *testing.T) {
	result := parseDelayMinutes(map[string]string{"delay_minutes": "-5"})
	if result != 0 {
		t.Errorf("expected 0 for negative, got %d", result)
	}
}

// Intent: Non-numeric value returns 0.
func TestParseDelayMinutes_NonNumeric(t *testing.T) {
	result := parseDelayMinutes(map[string]string{"delay_minutes": "abc"})
	if result != 0 {
		t.Errorf("expected 0 for non-numeric, got %d", result)
	}
}

// Intent: Value exceeding 1440 is clamped to 1440.
func TestParseDelayMinutes_ClampedAt1440(t *testing.T) {
	result := parseDelayMinutes(map[string]string{"delay_minutes": "9999"})
	if result != 1440 {
		t.Errorf("expected 1440, got %d", result)
	}
}

// Intent: Boundary values: 1440 passes, 1441 clamps.
func TestParseDelayMinutes_Boundary(t *testing.T) {
	result := parseDelayMinutes(map[string]string{"delay_minutes": "1440"})
	if result != 1440 {
		t.Errorf("expected 1440, got %d", result)
	}

	result = parseDelayMinutes(map[string]string{"delay_minutes": "1441"})
	if result != 1440 {
		t.Errorf("expected 1440 (clamped), got %d", result)
	}
}

// --- Reboot execution tests ---

// Intent: Immediate reboot with delay=0 passes "now" arg.
func TestReboot_ImmediateSuccess(t *testing.T) {
	runner := &mockRunner{}
	e := &RebootExecutor{runner: runner}

	result := e.Execute(context.Background(), map[string]string{
		"reason": "test reboot",
	})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if len(runner.calls) != 1 {
		t.Fatalf("expected 1 run call, got %d", len(runner.calls))
	}
	if runner.calls[0].Args[1] != "now" {
		t.Errorf("expected time arg 'now', got %q", runner.calls[0].Args[1])
	}
}

// Intent: Delayed reboot with delay=30 passes "+30" arg.
func TestReboot_DelayedSuccess(t *testing.T) {
	runner := &mockRunner{}
	e := &RebootExecutor{runner: runner}

	result := e.Execute(context.Background(), map[string]string{
		"delay_minutes": "30",
		"reason":        "scheduled",
	})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if len(runner.calls) != 1 {
		t.Fatalf("expected 1 run call, got %d", len(runner.calls))
	}
	if runner.calls[0].Args[1] != "+30" {
		t.Errorf("expected time arg '+30', got %q", runner.calls[0].Args[1])
	}
}

// Intent: Shutdown command failure returns error.
func TestReboot_ShutdownFails(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("permission denied"), fmt.Errorf("exit status 1")
		},
	}
	e := &RebootExecutor{runner: runner}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for shutdown failure")
	}
	if result.Error == nil {
		t.Error("expected non-nil error")
	}
}

// --- CheckUpdate execution tests ---

// Intent: apt found and updates available -> count reported.
func TestCheckUpdate_AptFound_UpdatesAvailable(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("Listing...\nlibssl3/jammy 3.0.2 amd64\ncurl/jammy 7.81 amd64\nopenssl/jammy 3.0 amd64"), nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &CheckUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "3 packages upgradable") == false {
		t.Errorf("expected '3 packages upgradable' in message, got %q", result.Message)
	}
}

// Intent: apt found but no updates -> 0 count.
func TestCheckUpdate_AptFound_NoUpdates(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("Listing..."), nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &CheckUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "0 packages upgradable") == false {
		t.Errorf("expected '0 packages upgradable' in message, got %q", result.Message)
	}
}

// Intent: apt list command fails -> error result.
func TestCheckUpdate_AptFound_CommandFails(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return nil, fmt.Errorf("apt broken")
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &CheckUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for apt failure")
	}
}

// Intent: apt not available, dnf found -> dnf path used.
func TestCheckUpdate_DnfFound(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("bash.x86_64 5.1.8 baseos\ncurl.x86_64 7.61 baseos"), nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"dnf": true}}
	e := &CheckUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if strings.Contains(result.Message, "dnf") == false {
		t.Errorf("expected 'dnf' in message, got %q", result.Message)
	}
}

// Intent: No package manager found -> error.
func TestCheckUpdate_NoPackageManager(t *testing.T) {
	runner := &mockRunner{}
	looker := &mockLooker{available: map[string]bool{}}
	e := &CheckUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for no package manager")
	}
	if strings.Contains(result.Error.Error(), "no supported package manager") == false {
		t.Errorf("expected 'no supported package manager' error, got %q", result.Error.Error())
	}
}

// --- InstallUpdate execution tests ---

// Intent: apt update and upgrade both succeed.
func TestInstallUpdate_AptSuccess(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("done"), nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
	if len(runner.calls) != 2 {
		t.Fatalf("expected 2 run calls (update + upgrade), got %d", len(runner.calls))
	}
}

// Intent: apt update fails -> error.
func TestInstallUpdate_AptUpdateFails(t *testing.T) {
	callNum := 0
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			callNum++
			if callNum == 1 {
				return []byte("failed"), fmt.Errorf("apt update failed")
			}

			return nil, nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for apt update failure")
	}
}

// Intent: apt update OK, apt upgrade fails -> error.
func TestInstallUpdate_AptUpgradeFails(t *testing.T) {
	callNum := 0
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			callNum++
			if callNum == 2 {
				return []byte("broken"), fmt.Errorf("apt upgrade failed")
			}

			return nil, nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"apt": true}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for apt upgrade failure")
	}
}

// Intent: dnf update succeeds.
func TestInstallUpdate_DnfSuccess(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return []byte("done"), nil
		},
	}
	looker := &mockLooker{available: map[string]bool{"dnf": true}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success == false {
		t.Errorf("expected Success=true, got error: %v", result.Error)
	}
}

// Intent: dnf update fails -> error.
func TestInstallUpdate_DnfFails(t *testing.T) {
	runner := &mockRunner{
		runFn: func(ctx context.Context, name string, args ...string) ([]byte, error) {
			return nil, fmt.Errorf("dnf failed")
		},
	}
	looker := &mockLooker{available: map[string]bool{"dnf": true}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for dnf failure")
	}
}

// Intent: No package manager available -> error.
func TestInstallUpdate_NoPackageManager(t *testing.T) {
	runner := &mockRunner{}
	looker := &mockLooker{available: map[string]bool{}}
	e := &InstallUpdateExecutor{runner: runner, looker: looker}

	result := e.Execute(context.Background(), map[string]string{})

	if result.Success {
		t.Error("expected Success=false for no package manager")
	}
}

// --- IsValid tests ---

// Intent: All defined command types are valid.
func TestIsValid_AllDefinedTypes(t *testing.T) {
	validTypes := []CommandType{
		CommandReboot,
		CommandKillProcess,
		CommandKillSession,
		CommandCheckUpdate,
		CommandInstallUpdate,
	}

	for _, ct := range validTypes {
		if ct.IsValid() == false {
			t.Errorf("expected %q to be valid", ct)
		}
	}
}

// Intent: Unknown command types are invalid.
func TestIsValid_UnknownType(t *testing.T) {
	invalid := []CommandType{
		"",
		"nonexistent",
		"REBOOT",
		"kill-process",
		"drop_database",
	}

	for _, ct := range invalid {
		if ct.IsValid() {
			t.Errorf("expected %q to be invalid", ct)
		}
	}
}

// --- IsDestructive tests ---

// Intent: Reboot and InstallUpdate are destructive.
func TestIsDestructive_RebootAndInstallUpdate(t *testing.T) {
	if CommandReboot.IsDestructive() == false {
		t.Error("expected Reboot to be destructive")
	}
	if CommandInstallUpdate.IsDestructive() == false {
		t.Error("expected InstallUpdate to be destructive")
	}
}

// Intent: Other commands are not destructive.
func TestIsDestructive_OtherCommands(t *testing.T) {
	nonDestructive := []CommandType{CommandKillProcess, CommandKillSession, CommandCheckUpdate}
	for _, ct := range nonDestructive {
		if ct.IsDestructive() {
			t.Errorf("expected %s to NOT be destructive", ct)
		}
	}
}
