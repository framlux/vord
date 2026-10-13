// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package machineid reads the host's systemd machine id.
package machineid

import (
	"os"
	"path/filepath"
	"strings"
)

// Path is where systemd keeps the machine id, relative to the filesystem root.
const Path = "etc/machine-id"

// uninitialized is what systemd leaves in the file until first boot has assigned an id. Every image carries it,
// so it says nothing about which host this is.
const uninitialized = "uninitialized"

// Read returns the trimmed contents of the machine id file under root, which is "/" for the real host and a
// directory in tests. An empty root means "/". A file that cannot be read gives "". The contents are returned as
// they are, including "uninitialized", because registration and the system-info report send the id to the
// server, which refuses an empty one.
func Read(root string) string {
	if root == "" {
		root = "/"
	}

	data, err := os.ReadFile(filepath.Join(root, Path))
	if err != nil {
		return ""
	}

	return strings.TrimSpace(string(data))
}

// ReadHostIdentity returns the machine id as a means of telling this host from another: Read, except that an id
// that identifies no host gives "". That is a file that cannot be read, is empty or still says "uninitialized".
// The reporter uses it to tell state it wrote itself from state copied from another host, where "" means "cannot
// tell" and never counts as a difference.
func ReadHostIdentity(root string) string {
	id := Read(root)
	if id == uninitialized {
		return ""
	}

	return id
}
