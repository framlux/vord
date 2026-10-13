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

// Read returns the trimmed contents of the machine id file under root, which is "/" for the real host and a
// directory in tests. An empty root means "/". A file that cannot be read gives "".
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
