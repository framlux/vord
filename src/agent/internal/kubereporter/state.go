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

	"github.com/framlux/vord/internal/machineid"
)

// StateFileName is the file, in the agent data directory, that marks a host as an enrolled reporter candidate.
const StateFileName = "k8s-reporter.json"

// PendingFileName is the file, in the agent data directory, that records the instance id of a token Secret before
// the Secret is created. It is the record that lets a later enrol, unenrol or uninstall find a Secret that an enrol
// created but never wrote the state file for, and it is removed once the state file holds the id or the Secret is
// gone.
const PendingFileName = "k8s-reporter-pending.json"

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
	// HostMachineID is the host's /etc/machine-id when the host enrolled, or "" when it had no usable one. A
	// file whose id differs from the host's current one was copied from another host.
	HostMachineID string `json:"host_machine_id"`
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

func pendingPath(dataDir string) string {
	return filepath.Join(dataDir, PendingFileName)
}

// uninitializedMachineID is what systemd leaves in /etc/machine-id until first boot has assigned one. Every
// image carries it, so it says nothing about which host this is.
const uninitializedMachineID = "uninitialized"

// hostMachineID returns this host's machine id, read under root, or "" when the host has none that tells it apart
// from another: the file is missing, unreadable, empty or still uninitialized.
func hostMachineID(root string) string {
	id := machineid.Read(root)
	if id == uninitializedMachineID {
		return ""
	}

	return id
}

// copiedFromAnotherHost reports whether a record that names recorded as the machine id of the host it was written
// on was written on a different host than the one named host. It is only ever true when both are known: a host
// without a usable machine id, as in a container without the file, can neither prove nor disprove a copy, and
// its records must keep working.
func copiedFromAnotherHost(recorded string, host string) bool {
	return (recorded != "") && (host != "") && (recorded != host)
}

// LocalRecordPresent reports whether this host holds a local record of having enrolled as a reporter candidate.
// It is how the agent tells a host that still has cluster objects of its own to clean up from one that does not:
// with remote commands off, only a host that holds a record polls for and accepts the cleanup commands, because
// every such host had remote commands on when it enrolled. This is the one place that decides what counts as a
// record, so a caller never looks at the files itself. The state file counts, and so does the pending file an enrol
// leaves when it created a token Secret and never wrote the state file. A record that cannot be loaded still
// counts, since the host enrolled once and the cluster may hold what it created, but one written on another host
// does not: that host enrolled, not this one.
func LocalRecordPresent(dataDir string) bool {
	return localRecordPresent(dataDir, hostMachineID("/"))
}

// localRecordPresent is LocalRecordPresent for a host whose machine id is host.
func localRecordPresent(dataDir string, host string) bool {
	for _, path := range []string{statePath(dataDir), pendingPath(dataDir)} {
		if regularFileExists(path) && (copiedFromAnotherHost(recordedHostMachineID(path), host) == false) {
			return true
		}
	}

	return false
}

// stateFileExists reports whether the state file is there, usable or not. Only a regular file counts, and any
// failure to find out answers false. It is the one definition of that fact: enrol asks it whether the host had a
// state file before the run, and LocalRecordPresent builds on it.
func stateFileExists(dataDir string) bool {
	return regularFileExists(statePath(dataDir))
}

func regularFileExists(path string) bool {
	info, err := os.Stat(path)

	return (err == nil) && info.Mode().IsRegular()
}

// readCapped reads a record file, at most maxStateFileBytes of it. An error never quotes the content, because the
// content may be a credential, and one for a file that does not exist satisfies errors.Is(err, os.ErrNotExist).
func readCapped(path string, name string) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, fmt.Errorf("open %s: %w", name, err)
	}

	defer func() { _ = file.Close() }()

	data, err := io.ReadAll(io.LimitReader(file, maxStateFileBytes+1))
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", name, err)
	}

	if len(data) > maxStateFileBytes {
		return nil, fmt.Errorf("%s is larger than %d bytes", name, maxStateFileBytes)
	}

	return data, nil
}

// recordedHostMachineID reads the host_machine_id of a record file however damaged the rest of it is, and is ""
// when the file says nothing usable about it.
func recordedHostMachineID(path string) string {
	data, err := readCapped(path, filepath.Base(path))
	if err != nil {
		return ""
	}

	var fields struct {
		HostMachineID string `json:"host_machine_id"`
	}

	if json.Unmarshal(data, &fields) != nil {
		return ""
	}

	return fields.HostMachineID
}

// LoadState reads the state file. A file that does not exist is not an error: it answers (State{}, false, nil).
// A file that cannot be read, is not valid or is missing a field answers an error that never quotes its
// content, because the content is a credential. host_machine_id must be present, though it may be "".
func LoadState(dataDir string) (State, bool, error) {
	sweepStaleTempFiles(dataDir, time.Now())

	data, err := readCapped(statePath(dataDir), StateFileName)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return State{}, false, nil
		}

		return State{}, false, err
	}

	var state State
	if err := json.Unmarshal(data, &state); err != nil {
		return State{}, false, fmt.Errorf("%s is not valid", StateFileName)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil {
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
	case isPresent(fields["host_machine_id"]) == false:
		return State{}, false, fmt.Errorf("%s has no host_machine_id", StateFileName)
	}

	return state, true, nil
}

// isPresent reports whether a raw JSON value was in the document at all: a key that is missing and one that is
// null both read as nothing.
func isPresent(raw json.RawMessage) bool {
	return (len(raw) > 0) && (string(raw) != "null")
}

// SaveState writes the state file with mode 0600. It writes a temporary file in the same directory, syncs it
// and renames it over the real one, so a reader sees the old file or the new one and never part of either.
func SaveState(dataDir string, s State) error {
	return writeRecord(dataDir, StateFileName, s)
}

// writeRecord encodes v as the file name in dataDir: a temporary file in the same directory with mode 0600, synced
// and renamed over the real one, and then the directory synced, so the file is either absent, the old one or the
// new one, and the new one is on disk before the call returns.
func writeRecord(dataDir string, name string, v any) error {
	sweepStaleTempFiles(dataDir, time.Now())

	data, err := json.Marshal(v)
	if err != nil {
		return fmt.Errorf("encode %s: %w", name, err)
	}

	temp, err := os.CreateTemp(dataDir, tempFilePrefix+"*"+tempFileSuffix)
	if err != nil {
		return fmt.Errorf("create %s: %w", name, err)
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
		return fmt.Errorf("protect %s: %w", name, err)
	}

	if _, err := temp.Write(append(data, '\n')); err != nil {
		return fmt.Errorf("write %s: %w", name, err)
	}

	if err := temp.Sync(); err != nil {
		return fmt.Errorf("sync %s: %w", name, err)
	}

	if err := temp.Close(); err != nil {
		return fmt.Errorf("close %s: %w", name, err)
	}

	if err := os.Rename(tempName, filepath.Join(dataDir, name)); err != nil {
		return fmt.Errorf("replace %s: %w", name, err)
	}

	renamed = true

	syncDirectory(dataDir)

	return nil
}

// recordedInstance is the part of a record file that names a token Secret and the host that wrote it. The pending
// file holds exactly this, and it is what is read back from a state file that cannot be loaded as a whole.
type recordedInstance struct {
	InstanceID    string `json:"instance_id"`
	HostMachineID string `json:"host_machine_id"`
}

// readRecordedInstance reads the instance id out of a record file as far as the file allows: the file must parse
// and its instance_id must have the shape of one, but nothing else about it is checked. It is false for a file that
// is missing, damaged or names an id that could not be a Secret name.
func readRecordedInstance(dataDir string, name string) (recordedInstance, bool) {
	data, err := readCapped(filepath.Join(dataDir, name), name)
	if err != nil {
		return recordedInstance{}, false
	}

	var record recordedInstance
	if json.Unmarshal(data, &record) != nil {
		return recordedInstance{}, false
	}

	if instanceIDPattern.MatchString(record.InstanceID) == false {
		return recordedInstance{}, false
	}

	return record, true
}

// recordedInstanceID returns the instance id this host wrote down for a token Secret that the state file does not
// (or cannot) name: the pending file's, else the one in a state file that cannot be loaded. A record written on
// another host is skipped as if it were not there, because its Secret is that host's.
func recordedInstanceID(dataDir string, host string) (string, bool) {
	for _, name := range []string{PendingFileName, StateFileName} {
		record, ok := readRecordedInstance(dataDir, name)
		if ok && (copiedFromAnotherHost(record.HostMachineID, host) == false) {
			return record.InstanceID, true
		}
	}

	return "", false
}

// savePending records instanceID as the id of a token Secret that is about to be created, in the same crash-safe
// way as the state file.
func savePending(dataDir string, instanceID string, host string) error {
	return writeRecord(dataDir, PendingFileName, recordedInstance{InstanceID: instanceID, HostMachineID: host})
}

// removeRecords deletes the state file and the pending file, both of them even when the first cannot be removed.
func removeRecords(dataDir string) error {
	return errors.Join(DeleteState(dataDir), deletePending(dataDir))
}

// deletePending removes the pending file. A file that is already gone is not an error.
func deletePending(dataDir string) error {
	if err := os.Remove(pendingPath(dataDir)); (err != nil) && (errors.Is(err, os.ErrNotExist) == false) {
		return fmt.Errorf("remove %s: %w", PendingFileName, err)
	}

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
