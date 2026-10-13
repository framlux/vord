// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package commands provides a dispatch system for executing remote commands on the agent.
package commands

import (
	"context"
	"fmt"
	"log/slog"
)

// CommandType identifies a command.
type CommandType string

const (
	CommandReboot        CommandType = "reboot"
	CommandKillProcess   CommandType = "kill_process"
	CommandKillSession   CommandType = "kill_session"
	CommandCheckUpdate   CommandType = "check_updates"
	CommandInstallUpdate CommandType = "install_updates"

	// The cluster reporter commands take no parameters. None of them is destructive: they change the cluster
	// and the agent's own state file, never the machine.
	CommandK8sReporterEnrol     CommandType = "k8s_reporter_enrol"
	CommandK8sReporterUnenrol   CommandType = "k8s_reporter_unenrol"
	CommandK8sReporterUninstall CommandType = "k8s_reporter_uninstall"
)

// validCommandTypes enumerates all recognized command types for validation.
var validCommandTypes = map[CommandType]bool{
	CommandReboot:        true,
	CommandKillProcess:   true,
	CommandKillSession:   true,
	CommandCheckUpdate:   true,
	CommandInstallUpdate: true,

	CommandK8sReporterEnrol:     true,
	CommandK8sReporterUnenrol:   true,
	CommandK8sReporterUninstall: true,
}

// IsValid returns true if the command type is a recognized, supported type.
func (ct CommandType) IsValid() bool {
	return validCommandTypes[ct]
}

// IsDestructive returns true if the command type kills the agent process or reboots the machine.
func (ct CommandType) IsDestructive() bool {
	return ct == CommandReboot || ct == CommandInstallUpdate
}

// IsCleanup returns true for the two cluster reporter commands that only remove what the reporter created:
// unenrol and uninstall. They are the only commands a host with remote commands off still accepts. The match is
// exact, so a spelling that differs in case is not a cleanup command.
func (ct CommandType) IsCleanup() bool {
	return (ct == CommandK8sReporterUnenrol) || (ct == CommandK8sReporterUninstall)
}

// Command represents a command to be executed.
type Command struct {
	Type   CommandType
	Params map[string]string
}

// Result represents the outcome of a command execution.
type Result struct {
	Success bool
	Message string
	Error   error

	// Rejected marks a command that was refused before it did anything, such as one carrying parameters it
	// does not accept. It is acknowledged as rejected rather than failed.
	Rejected bool
}

// Handler dispatches and executes commands.
type Handler struct {
	executors map[CommandType]Executor
}

// Executor executes a specific command type.
type Executor interface {
	Execute(ctx context.Context, params map[string]string) Result
}

// HandlerDeps holds what the executors need that is not a command runner.
type HandlerDeps struct {
	// KubeReporter carries out the cluster reporter commands. Nil makes those commands fail as unavailable.
	KubeReporter KubeReporter
}

// NewHandler creates a new command handler with all built-in executors registered.
func NewHandler(deps HandlerDeps) *Handler {
	h := &Handler{
		executors: make(map[CommandType]Executor),
	}

	h.executors[CommandReboot] = &RebootExecutor{}
	h.executors[CommandKillProcess] = &KillProcessExecutor{}
	h.executors[CommandKillSession] = &KillSessionExecutor{}
	h.executors[CommandCheckUpdate] = &CheckUpdateExecutor{}
	h.executors[CommandInstallUpdate] = &InstallUpdateExecutor{}
	h.executors[CommandK8sReporterEnrol] = newKubeReporterExecutor(deps.KubeReporter, kubeReporterEnrolTimeout, runEnrol)
	h.executors[CommandK8sReporterUnenrol] = newKubeReporterExecutor(deps.KubeReporter, kubeReporterUnenrolTimeout, runUnenrol)
	h.executors[CommandK8sReporterUninstall] = newKubeReporterExecutor(deps.KubeReporter, kubeReporterUninstallTimeout, runUninstall)

	return h
}

// Execute dispatches a command to the appropriate executor.
func (h *Handler) Execute(ctx context.Context, cmd Command) Result {
	executor, ok := h.executors[cmd.Type]
	if ok == false {

		return Result{
			Success: false,
			Error:   fmt.Errorf("unknown command type: %s", string(cmd.Type)),
		}
	}

	slog.Debug("executing command", "type", cmd.Type, "params", cmd.Params)
	result := executor.Execute(ctx, cmd.Params)

	if result.Error != nil {
		slog.Error("command failed", "type", cmd.Type, "error", result.Error)
	} else {
		slog.Info("command completed", "type", cmd.Type, "message", result.Message)
	}

	return result
}
