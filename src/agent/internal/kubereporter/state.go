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
	"strings"
	"time"
)

// StateFileName is the file, in the agent data directory, that marks a host as an enrolled reporter candidate.
const StateFileName = "k8s-reporter.json"

// maxStateFileBytes caps how much of the state file is read. A token, a CA and a URL are a few KB.
const maxStateFileBytes = 1 << 20

// tempFilePrefix and tempFileSuffix bracket the name of the temporary file a save writes before it renames.
const (
	tempFilePrefix = ".k8s-reporter-"
	tempFileSuffix = ".tmp"
)

// staleTempFileAge is how old a temporary file must be before a sweep removes it. A save takes milliseconds, so
// a file this old was left by a crash; younger ones may belong to a save in progress.
const staleTempFileAge = 10 * time.Minute

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

// LocalRecordPresent reports whether this host holds a local record of having enrolled as a reporter candidate.
// It is how the agent tells a host that still has cluster objects of its own to clean up from one that does not:
// with remote commands off, only a host that holds a record polls for and accepts the cleanup commands, because
// every such host had remote commands on when it enrolled. This is the one place that decides what counts as a
// record, so a caller never looks at the files itself. A record that cannot be loaded still counts, since the
// host enrolled once and the cluster may hold what it created.
func LocalRecordPresent(dataDir string) bool {
	return stateFileExists(dataDir)
}

// stateFileExists reports whether the state file is there, usable or not. Only a regular file counts, and any
// failure to find out answers false. It is the one definition of that fact: enrol asks it whether the host had a
// state file before the run, and LocalRecordPresent builds on it.
func stateFileExists(dataDir string) bool {
	info, err := os.Stat(statePath(dataDir))

	return (err == nil) && info.Mode().IsRegular()
}

// LoadState reads the state file. A file that does not exist is not an error: it answers (State{}, false, nil).
// A file that cannot be read, is not valid or is missing a field answers an error that never quotes its
// content, because the content is a credential.
func LoadState(dataDir string) (State, bool, error) {
	sweepStaleTempFiles(dataDir, time.Now())

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
	sweepStaleTempFiles(dataDir, time.Now())

	data, err := json.Marshal(s)
	if err != nil {
		return fmt.Errorf("encode %s: %w", StateFileName, err)
	}

	temp, err := os.CreateTemp(dataDir, tempFilePrefix+"*"+tempFileSuffix)
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

// sweepStaleTempFiles removes the temporary files that a save which crashed left behind, since each holds a
// token. It removes only files named like ours that are older than staleTempFileAge, and it is best effort: a
// failure to read the directory or to remove a file is ignored.
func sweepStaleTempFiles(dataDir string, now time.Time) {
	entries, err := os.ReadDir(dataDir)
	if err != nil {
		return
	}

	for _, entry := range entries {
		name := entry.Name()

		if (entry.Type().IsRegular() == false) || (strings.HasPrefix(name, tempFilePrefix) == false) || (strings.HasSuffix(name, tempFileSuffix) == false) {
			continue
		}

		info, err := entry.Info()
		if err != nil {
			continue
		}

		if now.Sub(info.ModTime()) > staleTempFileAge {
			_ = os.Remove(filepath.Join(dataDir, name))
		}
	}
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
