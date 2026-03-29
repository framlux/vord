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
	CommandReboot      CommandType = "reboot"
	CommandKillProcess CommandType = "kill_process"
	CommandKillSession CommandType = "kill_session"
	CommandCheckUpdate CommandType = "check_updates"
	CommandInstallUpdate CommandType = "install_updates"
)

// validCommandTypes enumerates all recognized command types for validation.
var validCommandTypes = map[CommandType]bool{
	CommandReboot:        true,
	CommandKillProcess:   true,
	CommandKillSession:   true,
	CommandCheckUpdate:   true,
	CommandInstallUpdate: true,
}

// IsValid returns true if the command type is a recognized, supported type.
func (ct CommandType) IsValid() bool {
	return validCommandTypes[ct]
}

// IsDestructive returns true if the command type kills the agent process or reboots the machine.
func (ct CommandType) IsDestructive() bool {
	return ct == CommandReboot || ct == CommandInstallUpdate
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
}

// Handler dispatches and executes commands.
type Handler struct {
	executors map[CommandType]Executor
}

// Executor executes a specific command type.
type Executor interface {
	Execute(ctx context.Context, params map[string]string) Result
}

// NewHandler creates a new command handler with all built-in executors registered.
func NewHandler() *Handler {
	h := &Handler{
		executors: make(map[CommandType]Executor),
	}

	h.executors[CommandReboot] = &RebootExecutor{}
	h.executors[CommandKillProcess] = &KillProcessExecutor{}
	h.executors[CommandKillSession] = &KillSessionExecutor{}
	h.executors[CommandCheckUpdate] = &CheckUpdateExecutor{}
	h.executors[CommandInstallUpdate] = &InstallUpdateExecutor{}

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

	slog.Info("executing command", "type", cmd.Type, "params", cmd.Params)
	result := executor.Execute(ctx, cmd.Params)

	if result.Error != nil {
		slog.Error("command failed", "type", cmd.Type, "error", result.Error)
	} else {
		slog.Info("command completed", "type", cmd.Type, "message", result.Message)
	}

	return result
}