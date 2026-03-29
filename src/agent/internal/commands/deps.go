// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"os/exec"
	"syscall"
)

// ProcessSignaler abstracts syscall.Kill for testability.
type ProcessSignaler interface {
	Signal(pid int, sig syscall.Signal) error
}

// CommandRunner abstracts exec.CommandContext for testability.
type CommandRunner interface {
	Run(ctx context.Context, name string, args ...string) ([]byte, error)
}

// PathLooker abstracts exec.LookPath for testability.
type PathLooker interface {
	LookPath(file string) (string, error)
}

// defaultSignaler uses the real syscall.Kill.
type defaultSignaler struct{}

func (defaultSignaler) Signal(pid int, sig syscall.Signal) error {
	return syscall.Kill(pid, sig)
}

// defaultRunner uses the real exec.CommandContext.
type defaultRunner struct{}

func (defaultRunner) Run(ctx context.Context, name string, args ...string) ([]byte, error) {
	return exec.CommandContext(ctx, name, args...).CombinedOutput()
}

// defaultLooker uses the real exec.LookPath.
type defaultLooker struct{}

func (defaultLooker) LookPath(file string) (string, error) {
	return exec.LookPath(file)
}
