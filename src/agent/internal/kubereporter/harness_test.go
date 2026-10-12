// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/pem"
	"fmt"
	"log/slog"
	"math/big"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kubelocal"
)

// fakeClock is the time source and sleeper the reporter waits with in tests: sleeping advances the clock and
// returns at once, so no test ever waits.
type fakeClock struct {
	mu      sync.Mutex
	now     time.Time
	sleeps  []time.Duration
	onSleep func()
}

func newFakeClock() *fakeClock {
	return &fakeClock{now: time.Date(2026, time.October, 11, 12, 0, 0, 0, time.UTC)}
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()

	return c.now
}

func (c *fakeClock) Sleep(ctx context.Context, d time.Duration) error {
	if err := ctx.Err(); err != nil {
		return err
	}

	c.mu.Lock()
	c.now = c.now.Add(d)
	c.sleeps = append(c.sleeps, d)
	hook := c.onSleep
	c.mu.Unlock()

	if hook != nil {
		hook()
	}

	return nil
}

// slept returns every duration slept so far.
func (c *fakeClock) slept() []time.Duration {
	c.mu.Lock()
	defer c.mu.Unlock()

	return append([]time.Duration(nil), c.sleeps...)
}

// totalSlept is the sum of every sleep.
func (c *fakeClock) totalSlept() time.Duration {
	var total time.Duration

	for _, d := range c.slept() {
		total += d
	}

	return total
}

// fakeReleaser records when it was asked to release the Lease, as the number of requests the API had served.
type fakeReleaser struct {
	api          *fakeAPI
	calls        int
	atRequestLen int
}

func (f *fakeReleaser) ReleaseAndStop(context.Context) {
	f.calls++
	f.atRequestLen = len(f.api.requestLog())
}

// harness wires a Reporter to a fake API, a data directory, an admin kubeconfig and a fake clock.
type harness struct {
	t          *testing.T
	api        *fakeAPI
	dataDir    string
	kubeconfig string
	clock      *fakeClock
	releaser   *fakeReleaser
	changed    int
	facts      kubelocal.Facts
	factsOK    bool
	reporter   *Reporter
}

func newHarness(t *testing.T) *harness {
	t.Helper()

	api := newFakeAPI(t)
	dataDir := t.TempDir()
	kubeconfig := filepath.Join(t.TempDir(), "admin.yaml")

	config := fmt.Sprintf(`apiVersion: v1
kind: Config
current-context: default
clusters:
- name: test
  cluster:
    server: %s
    certificate-authority-data: %s
users:
- name: admin
  user:
    token: %s
contexts:
- name: default
  context:
    cluster: test
    user: admin
`, api.url(), base64.StdEncoding.EncodeToString(api.caPEM), fakeAdminToken)

	if err := os.WriteFile(kubeconfig, []byte(config), 0o600); err != nil {
		t.Fatalf("write kubeconfig: %v", err)
	}

	h := &harness{
		t:          t,
		api:        api,
		dataDir:    dataDir,
		kubeconfig: kubeconfig,
		clock:      newFakeClock(),
		releaser:   &fakeReleaser{api: api},
		factsOK:    true,
		facts: kubelocal.Facts{
			Distro:                 "k3s",
			NodeName:               "cp1",
			HasKubelet:             true,
			ControlPlane:           true,
			AdminCredentialPresent: true,
			AdminKubeconfigPath:    kubeconfig,
		},
	}

	h.reporter = New(Options{
		DataDir:        dataDir,
		Facts:          func(context.Context) (kubelocal.Facts, bool) { return h.facts, h.factsOK },
		Releaser:       h.releaser,
		OnStateChanged: func(context.Context) { h.changed++ },
		Now:            h.clock.Now,
		Sleep:          h.clock.Sleep,
	})

	return h
}

// enrol runs an enrol and fails the test unless it succeeds.
func (h *harness) enrol() {
	h.t.Helper()

	result := h.reporter.Enrol(context.Background())
	if result.Success == false {
		h.t.Fatalf("enrol failed: %q (%v)", result.Message, result.Error)
	}
}

// state loads the state file and fails the test when it is missing or unusable.
func (h *harness) state() State {
	h.t.Helper()

	state, found, err := LoadState(h.dataDir)
	if err != nil {
		h.t.Fatalf("LoadState: %v", err)
	}

	if found == false {
		h.t.Fatal("no state file")
	}

	return state
}

// hasState reports whether the state file exists.
func (h *harness) hasState() bool {
	_, err := os.Stat(filepath.Join(h.dataDir, StateFileName))

	return err == nil
}

// secretPathOf is the path of this host's token Secret.
func (h *harness) secretPath() string {
	return secretPath(h.state().InstanceID)
}

// seedSecret stores a token Secret for another candidate, labelled as the agent labels its own.
func (h *harness) seedSecret(instanceID string, labelled bool) string {
	path := secretPath(instanceID)

	metadata := map[string]any{"name": tokenSecretPrefix + instanceID, "namespace": reporterNamespace}
	if labelled {
		metadata["labels"] = map[string]any{managedByLabel: managedByValue}
	}

	h.api.seed(path, map[string]any{"apiVersion": "v1", "kind": "Secret", "metadata": metadata, "type": serviceAccountTokenType})

	return path
}

// codeOf returns the reason code at the front of a failure message.
func codeOf(message string) string {
	code, _, _ := strings.Cut(message, ":")

	return code
}

// requireFailure fails the test unless result is a failure with the given reason code.
func requireFailure(t *testing.T, result commands.Result, code string) {
	t.Helper()

	if result.Success {
		t.Fatalf("expected failure %q, got success %q", code, result.Message)
	}

	if codeOf(result.Message) != code {
		t.Fatalf("failure code = %q (message %q), want %q", codeOf(result.Message), result.Message, code)
	}

	if result.Error == nil {
		t.Errorf("a failure must carry an error for the log")
	}
}

// requireSuccess fails the test unless result succeeded.
func requireSuccess(t *testing.T, result commands.Result) {
	t.Helper()

	if result.Success == false {
		t.Fatalf("expected success, got %q (%v)", result.Message, result.Error)
	}
}

// captureLogs sends the default logger to a buffer at debug level for the rest of the test.
func captureLogs(t *testing.T) *bytes.Buffer {
	t.Helper()

	previous := slog.Default()
	buffer := &bytes.Buffer{}
	slog.SetDefault(slog.New(slog.NewTextHandler(buffer, &slog.HandlerOptions{Level: slog.LevelDebug})))
	t.Cleanup(func() { slog.SetDefault(previous) })

	return buffer
}

var hexInstanceID = regexp.MustCompile(`^[0-9a-f]{16}$`)

// unrelatedCAPEM returns a self-signed CA certificate that verifies no test server. Every httptest TLS server
// shares one built-in certificate, so a different CA has to be made.
func unrelatedCAPEM(t *testing.T) []byte {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "unrelated test CA"},
		NotBefore:             time.Date(2026, time.January, 1, 0, 0, 0, 0, time.UTC),
		NotAfter:              time.Date(2036, time.January, 1, 0, 0, 0, 0, time.UTC),
		KeyUsage:              x509.KeyUsageCertSign,
		BasicConstraintsValid: true,
		IsCA:                  true,
	}

	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("create certificate: %v", err)
	}

	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
}
