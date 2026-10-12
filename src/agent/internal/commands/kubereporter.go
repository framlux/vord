// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package commands

import (
	"context"
	"errors"
	"time"
)

// Deadlines of the cluster reporter commands. Enrol and uninstall make many calls to the cluster and wait for a
// token and for namespaces, so they get longer than unenrol, which makes one.
const (
	kubeReporterEnrolTimeout     = 150 * time.Second
	kubeReporterUnenrolTimeout   = 60 * time.Second
	kubeReporterUninstallTimeout = 150 * time.Second
)

// Messages of results the executors produce themselves.
const (
	messageParametersNotAllowed = "parameters not allowed"
	messageReporterUnavailable  = "reporter_unavailable"
)

// KubeReporter installs, removes and tears down the cluster reporter. A failed Result carries its reason code in
// Message, which is what reaches the server.
type KubeReporter interface {
	Enrol(ctx context.Context) Result
	Unenrol(ctx context.Context) Result
	Uninstall(ctx context.Context) Result
}

func runEnrol(ctx context.Context, reporter KubeReporter) Result { return reporter.Enrol(ctx) }

func runUnenrol(ctx context.Context, reporter KubeReporter) Result { return reporter.Unenrol(ctx) }

func runUninstall(ctx context.Context, reporter KubeReporter) Result { return reporter.Uninstall(ctx) }

// kubeReporterExecutor runs one cluster reporter command. The three commands take no parameters, so a command
// that carries any is rejected before it does anything.
type kubeReporterExecutor struct {
	reporter KubeReporter
	timeout  time.Duration
	run      func(ctx context.Context, reporter KubeReporter) Result
}

func newKubeReporterExecutor(reporter KubeReporter, timeout time.Duration, run func(ctx context.Context, reporter KubeReporter) Result) *kubeReporterExecutor {
	return &kubeReporterExecutor{reporter: reporter, timeout: timeout, run: run}
}

// Execute rejects a command with parameters, and otherwise runs the reporter under the command's deadline.
func (e *kubeReporterExecutor) Execute(ctx context.Context, params map[string]string) Result {
	if len(params) > 0 {
		return Result{
			Success:  false,
			Rejected: true,
			Message:  messageParametersNotAllowed,
			Error:    errors.New(messageParametersNotAllowed),
		}
	}

	if e.reporter == nil {
		return Result{Success: false, Message: messageReporterUnavailable, Error: errors.New("no cluster reporter is configured on this agent")}
	}

	ctx, cancel := context.WithTimeout(ctx, e.timeout)
	defer cancel()

	return e.run(ctx, e.reporter)
}
