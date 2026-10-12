// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func sampleState() State {
	return State{
		InstanceID:    "0123456789abcdef",
		Server:        "https://10.0.0.1:6443",
		CACert:        "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
		Token:         "secret-token-value",
		TLSServerName: "kubernetes",
	}
}

// Intent: What is saved is what is loaded, and the file is private to its owner.
func TestState_RoundTrip_WritesPrivateFile(t *testing.T) {
	dir := t.TempDir()

	if err := SaveState(dir, sampleState()); err != nil {
		t.Fatalf("SaveState: %v", err)
	}

	info, err := os.Stat(filepath.Join(dir, StateFileName))
	if err != nil {
		t.Fatal(err)
	}

	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode = %v, want 0600", info.Mode().Perm())
	}

	got, found, err := LoadState(dir)
	if (err != nil) || (found == false) {
		t.Fatalf("LoadState = found %v, err %v", found, err)
	}

	if got != sampleState() {
		t.Errorf("loaded %+v, want %+v", got, sampleState())
	}
}

// Intent: The file is replaced in one step and no temporary file is left behind, so a reader never sees a
// half-written token.
func TestState_Save_ReplacesAtomicallyAndLeavesNoTemporaryFile(t *testing.T) {
	dir := t.TempDir()

	first := sampleState()
	second := sampleState()
	second.Token = "second-token"

	if err := SaveState(dir, first); err != nil {
		t.Fatal(err)
	}

	if err := SaveState(dir, second); err != nil {
		t.Fatal(err)
	}

	got, _, err := LoadState(dir)
	if err != nil {
		t.Fatal(err)
	}

	if got.Token != "second-token" {
		t.Errorf("token = %q", got.Token)
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}

	if (len(entries) != 1) || (entries[0].Name() != StateFileName) {
		t.Errorf("directory holds %v, want only %s", entries, StateFileName)
	}
}

// Intent: An existing file with looser permissions is tightened by a save.
func TestState_Save_TightensExistingFileMode(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, StateFileName)

	if err := os.WriteFile(path, []byte("{}"), 0o644); err != nil {
		t.Fatal(err)
	}

	if err := SaveState(dir, sampleState()); err != nil {
		t.Fatal(err)
	}

	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}

	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode = %v, want 0600", info.Mode().Perm())
	}
}

// Intent: A missing file is "not enrolled", not an error.
func TestState_Load_MissingFile_IsNotFound(t *testing.T) {
	got, found, err := LoadState(t.TempDir())

	if (err != nil) || found || (got != State{}) {
		t.Errorf("LoadState = %+v, %v, %v; want empty, not found, no error", got, found, err)
	}
}

// Intent: A file that is damaged or incomplete is an error that never echoes its content, and an instance id
// that could not be a Secret name is refused before it reaches a request path.
func TestState_Load_InvalidFile_IsError(t *testing.T) {
	cases := map[string]string{
		"not json":           "this is not json token=secret-token-value",
		"empty":              "",
		"missing token":      `{"instance_id":"0123456789abcdef","server":"https://x","ca_crt":"pem"}`,
		"missing server":     `{"instance_id":"0123456789abcdef","ca_crt":"pem","token":"t"}`,
		"missing ca":         `{"instance_id":"0123456789abcdef","server":"https://x","token":"t"}`,
		"short instance id":  `{"instance_id":"0123","server":"https://x","ca_crt":"pem","token":"t"}`,
		"uppercase id":       `{"instance_id":"0123456789ABCDEF","server":"https://x","ca_crt":"pem","token":"t"}`,
		"path in instanceid": `{"instance_id":"../../../etc/pass","server":"https://x","ca_crt":"pem","token":"t"}`,
	}

	for name, content := range cases {
		t.Run(name, func(t *testing.T) {
			dir := t.TempDir()

			if err := os.WriteFile(filepath.Join(dir, StateFileName), []byte(content), 0o600); err != nil {
				t.Fatal(err)
			}

			_, found, err := LoadState(dir)
			if err == nil {
				t.Fatal("expected an error")
			}

			if found {
				t.Error("an unusable file must not count as found")
			}

			if strings.Contains(err.Error(), "secret-token-value") {
				t.Errorf("the error echoes the file: %v", err)
			}
		})
	}
}

// Intent: Deleting is idempotent.
func TestState_Delete_RemovesFileAndTolerantOfMissing(t *testing.T) {
	dir := t.TempDir()

	if err := SaveState(dir, sampleState()); err != nil {
		t.Fatal(err)
	}

	if err := DeleteState(dir); err != nil {
		t.Fatalf("DeleteState: %v", err)
	}

	if _, err := os.Stat(filepath.Join(dir, StateFileName)); os.IsNotExist(err) == false {
		t.Errorf("the file survived: %v", err)
	}

	if err := DeleteState(dir); err != nil {
		t.Errorf("deleting a missing file: %v", err)
	}
}

// Intent: Saving into a directory that does not exist fails rather than creating it.
func TestState_Save_MissingDirectory_Fails(t *testing.T) {
	if err := SaveState(filepath.Join(t.TempDir(), "absent"), sampleState()); err == nil {
		t.Error("expected an error")
	}
}

// Intent: A State formatted any way or logged never shows the token or the CA.
func TestState_Formatting_RedactsSecrets(t *testing.T) {
	state := sampleState()

	forms := []string{
		fmt.Sprint(state),
		fmt.Sprintf("%v", state),
		fmt.Sprintf("%+v", state),
		fmt.Sprintf("%#v", state),
		fmt.Sprintf("%s", state),
		fmt.Sprint(&state),
	}

	for _, form := range forms {
		if strings.Contains(form, "secret-token-value") || strings.Contains(form, "BEGIN CERTIFICATE") {
			t.Errorf("formatted State leaks a secret: %s", form)
		}
	}

	var text strings.Builder

	slog.New(slog.NewTextHandler(&text, nil)).Info("state", "state", state)

	var structured strings.Builder

	slog.New(slog.NewJSONHandler(&structured, nil)).Info("state", "state", state)

	for _, logged := range []string{text.String(), structured.String()} {
		if strings.Contains(logged, "secret-token-value") || strings.Contains(logged, "BEGIN CERTIFICATE") {
			t.Errorf("logged State leaks a secret: %s", logged)
		}
	}

	if strings.Contains(fmt.Sprint(state), state.InstanceID) == false {
		t.Error("the redacted form should still identify the instance")
	}
}
