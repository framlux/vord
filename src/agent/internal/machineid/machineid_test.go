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

// Intent: Read returns what the file says, "uninitialized" included, because registration and the system-info
// report send it to a server that refuses an empty id.
func TestRead_Uninitialized_IsReturnedAsItIs(t *testing.T) {
	root := t.TempDir()
	writeMachineID(t, root, "uninitialized\n")

	if got := Read(root); got != "uninitialized" {
		t.Errorf("Read = %q, want uninitialized", got)
	}
}

// Intent: As a way to tell hosts apart, an id that systemd has not assigned yet is the same string on every image,
// so it is no id, and neither is a file that is missing, empty or blank. A real id is returned trimmed.
func TestReadHostIdentity_OnlyAnIDThatTellsHostsApartCounts(t *testing.T) {
	cases := map[string]struct {
		content string
		want    string
	}{
		"a real id":     {"0123456789abcdef0123456789abcdef\n", "0123456789abcdef0123456789abcdef"},
		"padded":        {"  0123456789abcdef0123456789abcdef \r\n", "0123456789abcdef0123456789abcdef"},
		"uninitialized": {"uninitialized\n", ""},
		"empty":         {"", ""},
		"blank":         {" \n", ""},
	}

	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			writeMachineID(t, root, tc.content)

			if got := ReadHostIdentity(root); got != tc.want {
				t.Errorf("ReadHostIdentity = %q, want %q", got, tc.want)
			}
		})
	}

	if got := ReadHostIdentity(t.TempDir()); got != "" {
		t.Errorf("ReadHostIdentity without the file = %q, want empty", got)
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
