// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"regexp"
)

// StateFileName is the file, in the agent data directory, that marks a host as an enrolled reporter candidate.
const StateFileName = "k8s-reporter.json"

// maxStateFileBytes caps how much of the state file is read. A token, a CA and a URL are a few KB.
const maxStateFileBytes = 1 << 20

// instanceIDPattern is what an instance id looks like: 16 lowercase hex characters. It is checked on load
// because the id becomes part of a Secret name and of a request path.
var instanceIDPattern = regexp.MustCompile(`^[0-9a-f]{16}$`)

// State is the content of k8s-reporter.json: what this candidate needs to talk to the cluster as the reporter.
// Formatting a State with any fmt verb, or logging it, shows the instance and server and redacts the secrets.
type State struct {
	InstanceID    string `json:"instance_id"`
	Server        string `json:"server"`
	CACert        string `json:"ca_crt"`
	Token         string `json:"token"`
	TLSServerName string `json:"tls_server_name,omitempty"`
}

// String describes the State without its token or CA.
func (s State) String() string {
	return fmt.Sprintf("kubereporter.State{InstanceID: %s, Server: %s, CA: [redacted], Token: [redacted]}", s.InstanceID, s.Server)
}

// GoString is the %#v form, identical to String so the Go-syntax dump cannot leak either.
func (s State) GoString() string {
	return s.String()
}

// LogValue is the form structured logging uses, so a State passed to slog is redacted by the JSON handler too.
func (s State) LogValue() slog.Value {
	return slog.StringValue(s.String())
}

func statePath(dataDir string) string {
	return filepath.Join(dataDir, StateFileName)
}

// LoadState reads the state file. A file that does not exist is not an error: it answers (State{}, false, nil).
// A file that cannot be read, is not valid or is missing a field answers an error that never quotes its
// content, because the content is a credential.
func LoadState(dataDir string) (State, bool, error) {
	file, err := os.Open(statePath(dataDir))
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return State{}, false, nil
		}

		return State{}, false, fmt.Errorf("open %s: %w", StateFileName, err)
	}

	defer func() { _ = file.Close() }()

	data, err := io.ReadAll(io.LimitReader(file, maxStateFileBytes+1))
	if err != nil {
		return State{}, false, fmt.Errorf("read %s: %w", StateFileName, err)
	}

	if len(data) > maxStateFileBytes {
		return State{}, false, fmt.Errorf("%s is larger than %d bytes", StateFileName, maxStateFileBytes)
	}

	var state State
	if err := json.Unmarshal(data, &state); err != nil {
		return State{}, false, fmt.Errorf("%s is not valid", StateFileName)
	}

	switch {
	case instanceIDPattern.MatchString(state.InstanceID) == false:
		return State{}, false, fmt.Errorf("%s has no valid instance_id", StateFileName)
	case state.Server == "":
		return State{}, false, fmt.Errorf("%s has no server", StateFileName)
	case state.CACert == "":
		return State{}, false, fmt.Errorf("%s has no ca_crt", StateFileName)
	case state.Token == "":
		return State{}, false, fmt.Errorf("%s has no token", StateFileName)
	}

	return state, true, nil
}

// SaveState writes the state file with mode 0600. It writes a temporary file in the same directory, syncs it
// and renames it over the real one, so a reader sees the old file or the new one and never part of either.
func SaveState(dataDir string, s State) error {
	data, err := json.Marshal(s)
	if err != nil {
		return fmt.Errorf("encode %s: %w", StateFileName, err)
	}

	temp, err := os.CreateTemp(dataDir, ".k8s-reporter-*.tmp")
	if err != nil {
		return fmt.Errorf("create %s: %w", StateFileName, err)
	}

	tempName := temp.Name()
	renamed := false

	defer func() {
		if renamed == false {
			_ = temp.Close()
			_ = os.Remove(tempName)
		}
	}()

	if err := temp.Chmod(0o600); err != nil {
		return fmt.Errorf("protect %s: %w", StateFileName, err)
	}

	if _, err := temp.Write(append(data, '\n')); err != nil {
		return fmt.Errorf("write %s: %w", StateFileName, err)
	}

	if err := temp.Sync(); err != nil {
		return fmt.Errorf("sync %s: %w", StateFileName, err)
	}

	if err := temp.Close(); err != nil {
		return fmt.Errorf("close %s: %w", StateFileName, err)
	}

	if err := os.Rename(tempName, statePath(dataDir)); err != nil {
		return fmt.Errorf("replace %s: %w", StateFileName, err)
	}

	renamed = true

	syncDirectory(dataDir)

	return nil
}

// syncDirectory flushes the directory entry of a rename. It is best effort: the file itself is already synced.
func syncDirectory(dir string) {
	handle, err := os.Open(dir)
	if err != nil {
		return
	}

	_ = handle.Sync()
	_ = handle.Close()
}

// DeleteState removes the state file. A file that is already gone is not an error.
func DeleteState(dataDir string) error {
	if err := os.Remove(statePath(dataDir)); (err != nil) && (errors.Is(err, os.ErrNotExist) == false) {
		return fmt.Errorf("remove %s: %w", StateFileName, err)
	}

	return nil
}
