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
// directory in tests. An empty root means "/". A file that cannot be read, is empty or still says uninitialized
// gives "", because none of those tells one host from another.
func Read(root string) string {
	if root == "" {
		root = "/"
	}

	data, err := os.ReadFile(filepath.Join(root, Path))
	if err != nil {
		return ""
	}

	id := strings.TrimSpace(string(data))
	if id == uninitialized {
		return ""
	}

	return id
}
