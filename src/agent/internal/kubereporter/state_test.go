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
	"time"
)

func sampleState() State {
	return State{
		InstanceID:    "0123456789abcdef",
		Server:        "https://10.0.0.1:6443",
		CACert:        "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
		Token:         "secret-token-value",
		TLSServerName: "kubernetes",
		HostMachineID: hostMachineIDValue,
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
		"missing token":      `{"instance_id":"0123456789abcdef","server":"https://x","ca_crt":"pem","host_machine_id":""}`,
		"missing server":     `{"instance_id":"0123456789abcdef","ca_crt":"pem","token":"t","host_machine_id":""}`,
		"missing ca":         `{"instance_id":"0123456789abcdef","server":"https://x","token":"t","host_machine_id":""}`,
		"short instance id":  `{"instance_id":"0123","server":"https://x","ca_crt":"pem","token":"t","host_machine_id":""}`,
		"uppercase id":       `{"instance_id":"0123456789ABCDEF","server":"https://x","ca_crt":"pem","token":"t","host_machine_id":""}`,
		"path in instanceid": `{"instance_id":"../../../etc/pass","server":"https://x","ca_crt":"pem","token":"t","host_machine_id":""}`,
		"no machine id":      `{"instance_id":"0123456789abcdef","server":"https://x","ca_crt":"pem","token":"t"}`,
		"null machine id":    `{"instance_id":"0123456789abcdef","server":"https://x","ca_crt":"pem","token":"t","host_machine_id":null}`,
		"numeric machine id": `{"instance_id":"0123456789abcdef","server":"https://x","ca_crt":"pem","token":"t","host_machine_id":12}`,
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

// writeTemp creates a leftover temporary file, as a crashed save would, with the given modification time.
func writeTemp(t *testing.T, dir string, name string, modified time.Time) string {
	t.Helper()

	path := filepath.Join(dir, name)

	if err := os.WriteFile(path, []byte(`{"token":"leftover-secret"}`), 0o600); err != nil {
		t.Fatal(err)
	}

	if err := os.Chtimes(path, modified, modified); err != nil {
		t.Fatal(err)
	}

	return path
}

// Intent: A temporary file left by a save that crashed holds a token, so a later save or load removes it, but a
// recent one, which may belong to a save in progress, and files that are not ours are left alone.
func TestState_SweepsStaleTemporaryFiles(t *testing.T) {
	now := time.Date(2026, time.October, 12, 12, 0, 0, 0, time.UTC)

	dir := t.TempDir()
	stale := writeTemp(t, dir, ".k8s-reporter-111.tmp", now.Add(-time.Hour))
	boundary := writeTemp(t, dir, ".k8s-reporter-222.tmp", now.Add(-staleTempFileAge-time.Second))
	recent := writeTemp(t, dir, ".k8s-reporter-333.tmp", now.Add(-time.Minute))
	other := writeTemp(t, dir, "agent.db.tmp", now.Add(-time.Hour))

	sweepStaleTempFiles(dir, now)

	for _, path := range []string{stale, boundary} {
		if _, err := os.Stat(path); os.IsNotExist(err) == false {
			t.Errorf("%s survived the sweep", filepath.Base(path))
		}
	}

	for _, path := range []string{recent, other} {
		if _, err := os.Stat(path); err != nil {
			t.Errorf("%s was removed: %v", filepath.Base(path), err)
		}
	}
}

// Intent: The sweep runs from both entry points, so a host that only ever loads, or only ever saves, cleans up.
func TestState_LoadAndSave_SweepLeftoverTemporaryFiles(t *testing.T) {
	ancient := time.Date(2000, time.January, 1, 0, 0, 0, 0, time.UTC)

	t.Run("load", func(t *testing.T) {
		dir := t.TempDir()
		stale := writeTemp(t, dir, ".k8s-reporter-abc.tmp", ancient)

		if _, _, err := LoadState(dir); err != nil {
			t.Fatal(err)
		}

		if _, err := os.Stat(stale); os.IsNotExist(err) == false {
			t.Error("LoadState left a stale temporary file")
		}
	})

	t.Run("save", func(t *testing.T) {
		dir := t.TempDir()
		stale := writeTemp(t, dir, ".k8s-reporter-abc.tmp", ancient)

		if err := SaveState(dir, sampleState()); err != nil {
			t.Fatal(err)
		}

		if _, err := os.Stat(stale); os.IsNotExist(err) == false {
			t.Error("SaveState left a stale temporary file")
		}
	})
}

// Intent: A host with no state file holds no local reporter record, and neither does one whose data directory
// does not exist.
func TestLocalRecordPresent_AbsentWhenNothingWasEnrolled(t *testing.T) {
	if LocalRecordPresent(t.TempDir()) {
		t.Error("an empty data directory reported a local record")
	}

	if LocalRecordPresent(filepath.Join(t.TempDir(), "missing")) {
		t.Error("a data directory that does not exist reported a local record")
	}
}

// Intent: A saved state file is a local record, and deleting it (what unenrol and uninstall do last) ends it.
func TestLocalRecordPresent_FollowsTheStateFile(t *testing.T) {
	dir := t.TempDir()

	if err := SaveState(dir, sampleState()); err != nil {
		t.Fatal(err)
	}

	if localRecordPresent(dir, hostMachineIDValue) == false {
		t.Fatal("a saved state file did not count as a local record")
	}

	if err := DeleteState(dir); err != nil {
		t.Fatal(err)
	}

	if localRecordPresent(dir, hostMachineIDValue) {
		t.Error("a deleted state file still counted as a local record")
	}
}

// Intent: The exported check reads the real host's machine id, so it is only exercised with records that do not
// name one: it must follow the files, whatever machine runs the test.
func TestLocalRecordPresent_ExportedCheckFollowsTheFilesOfAHostWithoutAMachineID(t *testing.T) {
	dir := t.TempDir()

	state := sampleState()
	state.HostMachineID = ""

	if err := SaveState(dir, state); err != nil {
		t.Fatal(err)
	}

	if LocalRecordPresent(dir) == false {
		t.Fatal("a saved state file did not count as a local record")
	}

	if err := DeleteState(dir); err != nil {
		t.Fatal(err)
	}

	if LocalRecordPresent(dir) {
		t.Fatal("a deleted state file still counted as a local record")
	}

	if err := savePending(dir, "0123456789abcdef", ""); err != nil {
		t.Fatal(err)
	}

	if LocalRecordPresent(dir) == false {
		t.Error("the exported check ignored the pending file")
	}
}

// Intent: The record is the file's existence, not its validity: a state file that cannot be loaded still means
// the host enrolled once and may hold cluster objects to clean up.
func TestLocalRecordPresent_CountsAFileThatCannotBeLoaded(t *testing.T) {
	dir := t.TempDir()

	if err := os.WriteFile(filepath.Join(dir, StateFileName), []byte("not json"), 0o600); err != nil {
		t.Fatal(err)
	}

	if localRecordPresent(dir, hostMachineIDValue) == false {
		t.Error("an unreadable state file did not count as a local record")
	}
}

// Intent: Only a file is a record; a directory that happens to carry the name is not.
func TestLocalRecordPresent_IgnoresADirectoryOfTheSameName(t *testing.T) {
	dir := t.TempDir()

	if err := os.Mkdir(filepath.Join(dir, StateFileName), 0o700); err != nil {
		t.Fatal(err)
	}

	if LocalRecordPresent(dir) {
		t.Error("a directory named like the state file counted as a local record")
	}
}

// Intent: The machine id is part of the file and may be empty, but it must be there: a file without the key was
// not written by this version and cannot say whose it is.
func TestState_Load_MachineIDMustBePresentButMayBeEmpty(t *testing.T) {
	dir := t.TempDir()

	empty := sampleState()
	empty.HostMachineID = ""

	if err := SaveState(dir, empty); err != nil {
		t.Fatal(err)
	}

	got, found, err := LoadState(dir)
	if (err != nil) || (found == false) {
		t.Fatalf("a state file with an empty host_machine_id: found %v, err %v", found, err)
	}

	if got != empty {
		t.Errorf("loaded %+v, want %+v", got, empty)
	}

	data, err := os.ReadFile(filepath.Join(dir, StateFileName))
	if err != nil {
		t.Fatal(err)
	}

	if strings.Contains(string(data), `"host_machine_id":""`) == false {
		t.Errorf("an empty machine id must still be written out: %s", data)
	}
}

// Intent: The pending file is saved the way the state file is: private to its owner, replaced in one step, with
// no temporary file left behind, and it reads back as the id and machine id that were saved.
func TestPending_RoundTrip_WritesPrivateFileAndLeavesNoTemporaryFile(t *testing.T) {
	dir := t.TempDir()

	if err := savePending(dir, "0123456789abcdef", hostMachineIDValue); err != nil {
		t.Fatalf("savePending: %v", err)
	}

	if err := savePending(dir, "fedcba9876543210", otherHostMachineID); err != nil {
		t.Fatalf("savePending again: %v", err)
	}

	info, err := os.Stat(filepath.Join(dir, PendingFileName))
	if err != nil {
		t.Fatal(err)
	}

	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode = %v, want 0600", info.Mode().Perm())
	}

	got, ok := readRecordedInstance(dir, PendingFileName)
	if (ok == false) || (got != recordedInstance{InstanceID: "fedcba9876543210", HostMachineID: otherHostMachineID}) {
		t.Errorf("read back %+v (%v)", got, ok)
	}

	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}

	if (len(entries) != 1) || (entries[0].Name() != PendingFileName) {
		t.Errorf("directory holds %v, want only %s", entries, PendingFileName)
	}

	if err := deletePending(dir); err != nil {
		t.Fatalf("deletePending: %v", err)
	}

	if err := deletePending(dir); err != nil {
		t.Errorf("deleting a missing pending file: %v", err)
	}

	if regularFileExists(pendingPath(dir)) {
		t.Error("the pending file survived its deletion")
	}
}

// Intent: A host_machine_id the pending file lacks reads as empty, which never marks a record as copied; only the
// instance id has to be right.
func TestPending_WithoutMachineID_StillNamesTheInstance(t *testing.T) {
	dir := t.TempDir()

	if err := os.WriteFile(filepath.Join(dir, PendingFileName), []byte(`{"instance_id":"0123456789abcdef"}`), 0o600); err != nil {
		t.Fatal(err)
	}

	got, ok := recordedInstanceID(dir, hostMachineIDValue)
	if (ok == false) || (got != "0123456789abcdef") {
		t.Errorf("recordedInstanceID = %q, %v", got, ok)
	}
}

// Intent: A pending file is a record just as a state file is, and only a regular file is.
func TestLocalRecordPresent_CountsThePendingFile(t *testing.T) {
	dir := t.TempDir()

	if localRecordPresent(dir, hostMachineIDValue) {
		t.Fatal("an empty data directory reported a local record")
	}

	if err := savePending(dir, "0123456789abcdef", hostMachineIDValue); err != nil {
		t.Fatal(err)
	}

	if localRecordPresent(dir, hostMachineIDValue) == false {
		t.Error("a pending file did not count as a local record")
	}

	if err := deletePending(dir); err != nil {
		t.Fatal(err)
	}

	if err := os.Mkdir(filepath.Join(dir, PendingFileName), 0o700); err != nil {
		t.Fatal(err)
	}

	if localRecordPresent(dir, hostMachineIDValue) {
		t.Error("a directory named like the pending file counted as a local record")
	}
}

// Intent: A record written on another host is that host's, not this one's, whichever file holds it and however
// damaged it is. It is what makes a flag-off clone stop polling for cleanup commands it could never act on. A
// record that cannot tell whose it is, or a host that cannot, still counts.
func TestLocalRecordPresent_SkipsRecordsCopiedFromAnotherHost(t *testing.T) {
	write := func(t *testing.T, name string, content string) string {
		t.Helper()

		dir := t.TempDir()

		if err := os.WriteFile(filepath.Join(dir, name), []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}

		return dir
	}

	cases := []struct {
		name    string
		file    string
		content string
		host    string
		want    bool
	}{
		{"state of this host", StateFileName, `{"host_machine_id":"` + hostMachineIDValue + `"}`, hostMachineIDValue, true},
		{"state of another host", StateFileName, `{"host_machine_id":"` + otherHostMachineID + `"}`, hostMachineIDValue, false},
		{"pending of another host", PendingFileName, `{"instance_id":"0123456789abcdef","host_machine_id":"` + otherHostMachineID + `"}`, hostMachineIDValue, false},
		{"pending of this host", PendingFileName, `{"instance_id":"0123456789abcdef","host_machine_id":"` + hostMachineIDValue + `"}`, hostMachineIDValue, true},
		{"state with no machine id recorded", StateFileName, `{"host_machine_id":""}`, hostMachineIDValue, true},
		{"state of another host, this host has no id", StateFileName, `{"host_machine_id":"` + otherHostMachineID + `"}`, "", true},
		{"state that is not json", StateFileName, "not json", hostMachineIDValue, true},
		{"state that says nothing about the host", StateFileName, `{"instance_id":"0123456789abcdef"}`, hostMachineIDValue, true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := localRecordPresent(write(t, tc.file, tc.content), tc.host); got != tc.want {
				t.Errorf("localRecordPresent = %v, want %v", got, tc.want)
			}
		})
	}

	t.Run("a copied state file does not hide this host's own pending file", func(t *testing.T) {
		dir := write(t, StateFileName, `{"host_machine_id":"`+otherHostMachineID+`"}`)

		if err := savePending(dir, "0123456789abcdef", hostMachineIDValue); err != nil {
			t.Fatal(err)
		}

		if localRecordPresent(dir, hostMachineIDValue) == false {
			t.Error("the host's own pending file was not counted because the state file was copied")
		}
	})
}

// Intent: A machine id counts only when it tells hosts apart: a missing, empty or still uninitialized file is "".
func TestHostMachineID_OnlyUsableIDsCount(t *testing.T) {
	cases := map[string]struct {
		content string
		want    string
	}{
		"a real id":     {hostMachineIDValue + "\n", hostMachineIDValue},
		"empty":         {"", ""},
		"blank":         {"  \n", ""},
		"uninitialized": {"uninitialized\n", ""},
	}

	for name, tc := range cases {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			writeMachineID(t, root, tc.content)

			if got := hostMachineID(root); got != tc.want {
				t.Errorf("hostMachineID = %q, want %q", got, tc.want)
			}
		})
	}

	if got := hostMachineID(t.TempDir()); got != "" {
		t.Errorf("hostMachineID without the file = %q, want empty", got)
	}
}

// Intent: A record is a copy only when both sides know a machine id and the ids differ.
func TestCopiedFromAnotherHost_NeedsBothIDsAndADifference(t *testing.T) {
	cases := []struct {
		recorded, host string
		want           bool
	}{
		{hostMachineIDValue, hostMachineIDValue, false},
		{otherHostMachineID, hostMachineIDValue, true},
		{"", hostMachineIDValue, false},
		{otherHostMachineID, "", false},
		{"", "", false},
	}

	for _, tc := range cases {
		if got := copiedFromAnotherHost(tc.recorded, tc.host); got != tc.want {
			t.Errorf("copiedFromAnotherHost(%q, %q) = %v, want %v", tc.recorded, tc.host, got, tc.want)
		}
	}
}

// Intent: The sweep that removes the temporary files of a save that crashed also covers a pending file's.
func TestPending_Save_SweepsStaleTemporaryFiles(t *testing.T) {
	dir := t.TempDir()
	stale := writeTemp(t, dir, ".k8s-reporter-abc.tmp", time.Date(2000, time.January, 1, 0, 0, 0, 0, time.UTC))

	if err := savePending(dir, "0123456789abcdef", ""); err != nil {
		t.Fatal(err)
	}

	if _, err := os.Stat(stale); os.IsNotExist(err) == false {
		t.Error("savePending left a stale temporary file")
	}
}

// Intent: A record written on another host is told apart from the host's own, whichever file holds it, so the
// agent can unenrol a clone without taking it for an enrolled host. A host that holds both kinds has its own, and
// a record that cannot be compared is the host's own.
func TestRecordsPresent_SeparatesOwnFromCopiedRecords(t *testing.T) {
	state := func(host string) string { return `{"host_machine_id":"` + host + `"}` }
	pending := func(host string) string {
		return `{"instance_id":"0123456789abcdef","host_machine_id":"` + host + `"}`
	}

	cases := []struct {
		name             string
		state, pending   string
		host             string
		wantOwn, wantCop bool
	}{
		{"nothing", "", "", hostMachineIDValue, false, false},
		{"own state", state(hostMachineIDValue), "", hostMachineIDValue, true, false},
		{"copied state", state(otherHostMachineID), "", hostMachineIDValue, false, true},
		{"copied pending", "", pending(otherHostMachineID), hostMachineIDValue, false, true},
		{"copied state, own pending", state(otherHostMachineID), pending(hostMachineIDValue), hostMachineIDValue, true, true},
		{"damaged copied state", `{"instance_id":"0123456789abcdef","host_machine_id":"` + otherHostMachineID + `"}`, "", hostMachineIDValue, false, true},
		{"state that says nothing about its host", `not json`, "", hostMachineIDValue, true, false},
		{"another host's id, this host has none", state(otherHostMachineID), "", "", true, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			dir := t.TempDir()

			if tc.state != "" {
				if err := os.WriteFile(filepath.Join(dir, StateFileName), []byte(tc.state), 0o600); err != nil {
					t.Fatal(err)
				}
			}

			if tc.pending != "" {
				if err := os.WriteFile(filepath.Join(dir, PendingFileName), []byte(tc.pending), 0o600); err != nil {
					t.Fatal(err)
				}
			}

			if got := localRecordPresent(dir, tc.host); got != tc.wantOwn {
				t.Errorf("localRecordPresent = %v, want %v", got, tc.wantOwn)
			}

			if got := copiedRecordPresent(dir, tc.host); got != tc.wantCop {
				t.Errorf("copiedRecordPresent = %v, want %v", got, tc.wantCop)
			}
		})
	}
}

// Intent: The exported check reads the real host's machine id, so it is only exercised with a record that names
// none, which is never a copy on any machine, and with a directory that holds nothing.
func TestCopiedRecordPresent_ExportedCheckIgnoresRecordsThatNameNoHost(t *testing.T) {
	dir := t.TempDir()

	if CopiedRecordPresent(dir) {
		t.Error("an empty data directory reported a copied record")
	}

	state := sampleState()
	state.HostMachineID = ""

	if err := SaveState(dir, state); err != nil {
		t.Fatal(err)
	}

	if CopiedRecordPresent(dir) {
		t.Error("a state file that names no host was taken for a copy")
	}
}

// Intent: A directory that cannot be synced after a record is written is not silent, although the write still
// succeeds: the pending file is what makes a crash after the Secret create recoverable.
func TestSyncDirectory_FailureIsLogged(t *testing.T) {
	logs := captureLogs(t)

	syncDirectory(filepath.Join(t.TempDir(), "missing"))

	if strings.Contains(logs.String(), "could not open the data directory") == false {
		t.Errorf("no warning was logged for a directory that could not be opened: %s", logs.String())
	}
}
