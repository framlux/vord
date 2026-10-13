// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package machineid

import (
	"os"
	"path/filepath"
	"testing"
)

func writeMachineID(t *testing.T, root string, content string) {
	t.Helper()

	if err := os.MkdirAll(filepath.Join(root, "etc"), 0o755); err != nil {
		t.Fatal(err)
	}

	if err := os.WriteFile(filepath.Join(root, Path), []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

// Intent: The id is the file's content with the trailing newline systemd writes removed.
func TestRead_TrimsTheFile(t *testing.T) {
	cases := map[string]string{
		"newline":        "0123456789abcdef0123456789abcdef\n",
		"no newline":     "0123456789abcdef0123456789abcdef",
		"padded":         "  0123456789abcdef0123456789abcdef \r\n",
		"two final eols": "0123456789abcdef0123456789abcdef\n\n",
	}

	for name, content := range cases {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			writeMachineID(t, root, content)

			if got := Read(root); got != "0123456789abcdef0123456789abcdef" {
				t.Errorf("Read = %q", got)
			}
		})
	}
}

// Intent: A machine id that systemd has not assigned yet is the same string on every image, so it is no id.
func TestRead_Uninitialized_IsEmpty(t *testing.T) {
	root := t.TempDir()
	writeMachineID(t, root, "uninitialized\n")

	if got := Read(root); got != "" {
		t.Errorf("Read = %q, want empty", got)
	}
}

// Intent: A file that is missing or empty gives no id rather than an error, so a container without the file works.
func TestRead_MissingOrEmptyFile_IsEmpty(t *testing.T) {
	if got := Read(t.TempDir()); got != "" {
		t.Errorf("Read of a root without the file = %q, want empty", got)
	}

	root := t.TempDir()
	writeMachineID(t, root, "\n")

	if got := Read(root); got != "" {
		t.Errorf("Read of an empty file = %q, want empty", got)
	}
}

// Intent: A directory where the file should be is not an id.
func TestRead_DirectoryInPlaceOfTheFile_IsEmpty(t *testing.T) {
	root := t.TempDir()

	if err := os.MkdirAll(filepath.Join(root, Path), 0o755); err != nil {
		t.Fatal(err)
	}

	if got := Read(root); got != "" {
		t.Errorf("Read = %q, want empty", got)
	}
}
