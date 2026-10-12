// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build unix

package kube

import (
	"os"
	"path/filepath"
	"syscall"
	"testing"
	"time"
)

// failIfBlocks runs read and fails the test when it does not return, which is what opening a FIFO that has no
// writer does. The wait only bounds how long a regression can hang the test; a correct implementation returns
// at once. Opening the FIFO for writing afterwards releases the stuck reader so the test can end.
func failIfBlocks(t *testing.T, fifo string, read func() error) error {
	t.Helper()

	done := make(chan error, 1)

	go func() { done <- read() }()

	select {
	case err := <-done:
		return err
	case <-time.After(10 * time.Second):
		writer, err := os.OpenFile(fifo, os.O_WRONLY, 0)
		if err == nil {
			_ = writer.Close()
		}

		t.Fatalf("reading %s blocked", fifo)

		return nil
	}
}

// Intent: the default reader refuses a FIFO without opening it, so a path that has been swapped for a FIFO cannot
// hang the agent while it reads a CA, a key or a token.
func TestReadBoundedFileRefusesFifo(t *testing.T) {
	fifo := filepath.Join(t.TempDir(), "named-pipe")
	if err := syscall.Mkfifo(fifo, 0o600); err != nil {
		t.Fatalf("mkfifo: %v", err)
	}

	err := failIfBlocks(t, fifo, func() error {
		_, err := readBoundedFile(fifo)

		return err
	})
	if err == nil {
		t.Errorf("a FIFO was read as if it were a file")
	}
}

// Intent: LoadKubeconfig with the default reader treats a FIFO named by the kubeconfig as an unreadable file
// rather than waiting on it, and also refuses a FIFO as the kubeconfig itself.
func TestLoadKubeconfigDefaultReaderRefusesFifo(t *testing.T) {
	dir := t.TempDir()

	fifo := filepath.Join(dir, "ca.crt")
	if err := syscall.Mkfifo(fifo, 0o600); err != nil {
		t.Fatalf("mkfifo: %v", err)
	}

	kubeconfig := `current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority: ca.crt}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`

	path := filepath.Join(dir, "admin.conf")
	if err := os.WriteFile(path, []byte(kubeconfig), 0o600); err != nil {
		t.Fatal(err)
	}

	err := failIfBlocks(t, fifo, func() error {
		_, err := LoadKubeconfig(path, nil)

		return err
	})
	if err == nil {
		t.Errorf("a FIFO CA file must fail the load")
	}

	pipeConfig := filepath.Join(dir, "pipe.conf")
	if err := syscall.Mkfifo(pipeConfig, 0o600); err != nil {
		t.Fatalf("mkfifo: %v", err)
	}

	err = failIfBlocks(t, pipeConfig, func() error {
		_, err := LoadKubeconfig(pipeConfig, nil)

		return err
	})
	if err == nil {
		t.Errorf("a FIFO kubeconfig must fail the load")
	}
}

// Intent: a symbolic link to a regular file is still read, because kubeconfigs are often linked into place.
func TestReadBoundedFileFollowsSymlinkToRegularFile(t *testing.T) {
	dir := t.TempDir()

	target := filepath.Join(dir, "real")
	if err := os.WriteFile(target, []byte("content"), 0o600); err != nil {
		t.Fatal(err)
	}

	link := filepath.Join(dir, "link")
	if err := os.Symlink(target, link); err != nil {
		t.Fatal(err)
	}

	data, err := readBoundedFile(link)
	if err != nil || string(data) != "content" {
		t.Errorf("readBoundedFile(link) = %q, %v", data, err)
	}
}

// Intent: a file over the cap is refused, not truncated.
func TestReadBoundedFileRefusesOversizedFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "big")
	if err := os.WriteFile(path, make([]byte, maxKubeconfigFileBytes+1), 0o600); err != nil {
		t.Fatal(err)
	}

	if _, err := readBoundedFile(path); err == nil {
		t.Errorf("an oversized file was read")
	}

	if err := os.WriteFile(path, make([]byte, maxKubeconfigFileBytes), 0o600); err != nil {
		t.Fatal(err)
	}

	if data, err := readBoundedFile(path); err != nil || len(data) != maxKubeconfigFileBytes {
		t.Errorf("a file of exactly the cap failed: %d bytes, %v", len(data), err)
	}
}
