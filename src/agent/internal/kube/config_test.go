// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"bytes"
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// commonBadInputs are inputs no parser may panic on. A kubeconfig comes from arbitrary hosts, so it can hold
// truncated data, binary garbage, NUL bytes or oversized lines.
var commonBadInputs = []string{
	"",                         // empty
	"\x00\x01\x02\xff\xfe",     // binary garbage
	"\x00\x00\x00\x00",         // NUL-filled
	strings.Repeat("a", 1<<20), // extremely long single line (1MB)
	"   \n\t\n  ",              // only whitespace
	"\n\n\n\n",                 // only newlines
	"\r\n\r\n",                 // Windows line endings
	"\t\t\t\t",                 // tab-heavy
	"cpu \xf0\x9f\x94\xa5 100", // Unicode/emoji
	"-1",                       // negative number
	"99999999999999999999",     // overflow number
}

// truncatedInput returns the first line of a format with more data after it.
func truncatedInput(fullInput string) string {
	idx := strings.Index(fullInput, "\n")
	if idx < 0 {
		return fullInput
	}

	return fullInput[:idx]
}

const kubeconfigDir = "/home/admin/.kube"

const kubeconfigPath = kubeconfigDir + "/config"

// fakeFiles serves files from a map and records every path read, so a test can assert that a file was or was not
// opened.
type fakeFiles struct {
	files map[string][]byte
	reads []string
}

func (f *fakeFiles) read(path string) ([]byte, error) {
	f.reads = append(f.reads, path)

	data, found := f.files[path]
	if found == false {
		return nil, fmt.Errorf("open %s: %w", path, os.ErrNotExist)
	}

	return data, nil
}

func (f *fakeFiles) wasRead(path string) bool {
	for _, read := range f.reads {
		if read == path {
			return true
		}
	}

	return false
}

func b64(value string) string {
	return base64.StdEncoding.EncodeToString([]byte(value))
}

// loadFrom loads kubeconfig text placed at kubeconfigPath, with extra files alongside.
func loadFrom(t *testing.T, kubeconfig string, extra map[string][]byte) (Config, *fakeFiles, error) {
	t.Helper()

	files := &fakeFiles{files: map[string][]byte{kubeconfigPath: []byte(kubeconfig)}}
	for path, data := range extra {
		files.files[path] = data
	}

	cfg, err := LoadKubeconfig(kubeconfigPath, files.read)

	return cfg, files, err
}

func mustLoad(t *testing.T, kubeconfig string, extra map[string][]byte) (Config, *fakeFiles) {
	t.Helper()

	cfg, files, err := loadFrom(t, kubeconfig, extra)
	if err != nil {
		t.Fatalf("LoadKubeconfig: %v", err)
	}

	return cfg, files
}

// Intent: an admin kubeconfig with everything embedded loads every credential field.
func TestLoadKubeconfigReadsEmbeddedData(t *testing.T) {
	kubeconfig := fmt.Sprintf(`apiVersion: v1
kind: Config
current-context: admin@prod
clusters:
- name: prod
  cluster:
    server: https://10.0.0.1:6443
    tls-server-name: kubernetes.internal
    certificate-authority-data: %s
contexts:
- name: admin@prod
  context:
    cluster: prod
    user: admin
users:
- name: admin
  user:
    client-certificate-data: %s
    client-key-data: %s
    token: inline-token
`, b64("CA-PEM"), b64("CERT-PEM"), b64("KEY-PEM"))

	cfg, files := mustLoad(t, kubeconfig, nil)

	want := Config{
		Server:        "https://10.0.0.1:6443",
		CAData:        []byte("CA-PEM"),
		TLSServerName: "kubernetes.internal",
		ClientCert:    []byte("CERT-PEM"),
		ClientKey:     []byte("KEY-PEM"),
		Token:         "inline-token",
	}

	if cfg.Server != want.Server || cfg.TLSServerName != want.TLSServerName || cfg.Token != want.Token {
		t.Errorf("Config = %v, want server %q, tls-server-name %q, token set", cfg, want.Server, want.TLSServerName)
	}

	if bytes.Equal(cfg.CAData, want.CAData) == false || bytes.Equal(cfg.ClientCert, want.ClientCert) == false || bytes.Equal(cfg.ClientKey, want.ClientKey) == false {
		t.Errorf("Config PEM fields = %q %q %q, want %q %q %q", cfg.CAData, cfg.ClientCert, cfg.ClientKey, want.CAData, want.ClientCert, want.ClientKey)
	}

	if len(files.reads) != 1 {
		t.Errorf("read %v, want only the kubeconfig itself", files.reads)
	}
}

// Intent: paths resolve against the kubeconfig's directory when relative and are used as written when absolute.
func TestLoadKubeconfigResolvesPaths(t *testing.T) {
	kubeconfig := `current-context: c
clusters:
- name: k
  cluster:
    server: https://k:6443
    certificate-authority: pki/ca.crt
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
    client-certificate: ../certs/admin.crt
    client-key: /etc/keys/admin.key
`

	extra := map[string][]byte{
		kubeconfigDir + "/pki/ca.crt":         []byte("CA"),
		"/home/admin/certs/admin.crt":         []byte("CERT"),
		"/etc/keys/admin.key":                 []byte("KEY"),
		kubeconfigDir + "/../certs/admin.crt": []byte("WRONG-UNCLEANED"),
	}

	cfg, _ := mustLoad(t, kubeconfig, extra)

	if string(cfg.CAData) != "CA" || string(cfg.ClientCert) != "CERT" || string(cfg.ClientKey) != "KEY" {
		t.Errorf("Config PEM fields = %q %q %q, want CA CERT KEY", cfg.CAData, cfg.ClientCert, cfg.ClientKey)
	}
}

// Intent: inline data wins over a path, and the path is then never opened.
func TestLoadKubeconfigInlineDataWinsOverPath(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster:
    server: https://k:6443
    certificate-authority: /etc/ca.crt
    certificate-authority-data: %s
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
    client-certificate: /etc/admin.crt
    client-certificate-data: %s
    client-key: /etc/admin.key
    client-key-data: %s
`, b64("INLINE-CA"), b64("INLINE-CERT"), b64("INLINE-KEY"))

	extra := map[string][]byte{
		"/etc/ca.crt":    []byte("FILE-CA"),
		"/etc/admin.crt": []byte("FILE-CERT"),
		"/etc/admin.key": []byte("FILE-KEY"),
	}

	cfg, files := mustLoad(t, kubeconfig, extra)

	if string(cfg.CAData) != "INLINE-CA" || string(cfg.ClientCert) != "INLINE-CERT" || string(cfg.ClientKey) != "INLINE-KEY" {
		t.Errorf("Config PEM fields = %q %q %q, want the inline values", cfg.CAData, cfg.ClientCert, cfg.ClientKey)
	}

	for _, path := range []string{"/etc/ca.crt", "/etc/admin.crt", "/etc/admin.key"} {
		if files.wasRead(path) {
			t.Errorf("%s was read although inline data was present", path)
		}
	}
}

// Intent: a token file is read and its trailing newline removed, because a newline in a bearer header is invalid.
// An inline token wins and the file is then left alone.
func TestLoadKubeconfigTokenFile(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
    tokenFile: token
`, b64("CA"))

	extra := map[string][]byte{kubeconfigDir + "/token": []byte("  file-token\r\n")}

	cfg, files := mustLoad(t, kubeconfig, extra)

	if cfg.Token != "file-token" {
		t.Errorf("Token = %q, want file-token", cfg.Token)
	}

	if files.wasRead(kubeconfigDir+"/token") == false {
		t.Errorf("the token file was not read")
	}

	inline := strings.Replace(kubeconfig, "tokenFile: token", "tokenFile: token\n    token: inline-token", 1)

	cfg, files = mustLoad(t, inline, extra)

	if cfg.Token != "inline-token" {
		t.Errorf("Token = %q, want the inline token to win", cfg.Token)
	}

	if files.wasRead(kubeconfigDir + "/token") {
		t.Errorf("the token file was read although an inline token was present")
	}
}

// Intent: an inline token written as a block scalar ends in a newline, which is removed rather than refused later.
func TestLoadKubeconfigInlineTokenIsTrimmed(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
    token: |
      block-token
`, b64("CA"))

	cfg, _ := mustLoad(t, kubeconfig, nil)

	if cfg.Token != "block-token" {
		t.Errorf("Token = %q, want block-token", cfg.Token)
	}
}

func TestLoadKubeconfigTokenFileProblems(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
    tokenFile: token
`, b64("CA"))

	t.Run("missing file", func(t *testing.T) {
		_, _, err := loadFrom(t, kubeconfig, nil)
		if (err == nil) || (errors.Is(err, os.ErrNotExist) == false) {
			t.Errorf("err = %v, want one wrapping os.ErrNotExist", err)
		}
	})

	t.Run("empty file", func(t *testing.T) {
		_, _, err := loadFrom(t, kubeconfig, map[string][]byte{kubeconfigDir + "/token": []byte(" \n")})
		if err == nil {
			t.Errorf("an empty token file must fail")
		}
	})
}

// Intent: current-context picks its context; with none, or one that does not exist, the first context is used.
func TestLoadKubeconfigContextSelection(t *testing.T) {
	template := fmt.Sprintf(`%%s
clusters:
- name: one
  cluster: {server: "https://one:6443", certificate-authority-data: %[1]s}
- name: two
  cluster: {server: "https://two:6443", certificate-authority-data: %[1]s}
contexts:
- name: first
  context: {cluster: one, user: u1}
- name: second
  context: {cluster: two, user: u2}
users:
- name: u1
  user: {token: token-one}
- name: u2
  user: {token: token-two}
`, b64("CA"))

	cases := []struct {
		name        string
		header      string
		wantServer  string
		wantToken   string
		wantSuccess bool
	}{
		{"current-context names the second", "current-context: second", "https://two:6443", "token-two", true},
		{"current-context names the first", "current-context: first", "https://one:6443", "token-one", true},
		{"no current-context", "kind: Config", "https://one:6443", "token-one", true},
		{"current-context names a missing context", "current-context: nowhere", "https://one:6443", "token-one", true},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cfg, _ := mustLoad(t, fmt.Sprintf(template, tc.header), nil)

			if cfg.Server != tc.wantServer || cfg.Token != tc.wantToken {
				t.Errorf("got server %q and a different token, want %q", cfg.Server, tc.wantServer)
			}
		})
	}
}

// Intent: a context that names a cluster or user the file does not define is an error, because using another
// entry would send credentials to the wrong cluster.
func TestLoadKubeconfigDoesNotGuessMissingTargets(t *testing.T) {
	base := fmt.Sprintf(`current-context: c
clusters:
- name: real
  cluster: {server: "https://real:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: %%s, user: %%s}
users:
- name: real
  user: {token: t}
`, b64("CA"))

	for _, tc := range []struct{ name, cluster, user string }{
		{"missing cluster", "ghost", "real"},
		{"missing user", "real", "ghost"},
		{"empty cluster", `""`, "real"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if _, _, err := loadFrom(t, fmt.Sprintf(base, tc.cluster, tc.user), nil); err == nil {
				t.Errorf("expected an error for a context naming %s", tc.name)
			}
		})
	}

	t.Run("no contexts at all", func(t *testing.T) {
		if _, _, err := loadFrom(t, "clusters: []\nusers: []\n", nil); err == nil {
			t.Errorf("a kubeconfig without a context must fail")
		}
	})
}

// Intent: an exec plugin, auth provider or username/password user is refused with ErrUnsupportedAuth, before any
// credential file is opened.
func TestLoadKubeconfigRejectsUnsupportedAuth(t *testing.T) {
	template := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user:
%%s
`, b64("CA"))

	cases := map[string]string{
		"exec": `    exec:
      apiVersion: client.authentication.k8s.io/v1
      command: aws
      args: [eks, get-token]`,
		"auth-provider": `    auth-provider:
      name: gcp
      config: {access-token: abc}`,
		"username and password": `    username: admin
    password: hunter2`,
		"username only":           `    username: admin`,
		"password only":           `    password: hunter2`,
		"exec with token":         "    token: t\n    exec: {command: helper}",
		"exec with a certificate": "    client-certificate: /etc/admin.crt\n    client-key: /etc/admin.key\n    exec: {command: helper}",
	}

	for name, user := range cases {
		t.Run(name, func(t *testing.T) {
			_, files, err := loadFrom(t, fmt.Sprintf(template, user), map[string][]byte{"/etc/admin.crt": []byte("c"), "/etc/admin.key": []byte("k")})
			if errors.Is(err, ErrUnsupportedAuth) == false {
				t.Errorf("err = %v, want ErrUnsupportedAuth", err)
			}

			if files.wasRead("/etc/admin.crt") || files.wasRead("/etc/admin.key") {
				t.Errorf("credential files were read for an unsupported user")
			}
		})
	}

	t.Run("null exec is no exec", func(t *testing.T) {
		if _, _, err := loadFrom(t, fmt.Sprintf(template, "    token: t\n    exec:"), nil); err != nil {
			t.Errorf("an empty exec key must not block a token user: %v", err)
		}
	})
}

// Intent: a kubeconfig that disables certificate verification is refused, whatever else it holds and however the
// flag is spelled, and nothing reads files for it.
func TestLoadKubeconfigRejectsInsecureSkipTLSVerify(t *testing.T) {
	template := `current-context: c
clusters:
- name: k
  cluster:
    server: https://k:6443
%s
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`

	ca := "    certificate-authority-data: " + b64("CA") + "\n"

	cases := map[string]string{
		"true":                   "    insecure-skip-tls-verify: true",
		"true with a CA":         ca + "    insecure-skip-tls-verify: true",
		"quoted true":            `    insecure-skip-tls-verify: "true"`,
		"capitalised string":     `    insecure-skip-tls-verify: "True"`,
		"number":                 "    insecure-skip-tls-verify: 1",
		"unrecognised string":    `    insecure-skip-tls-verify: "yes please"`,
		"true with a missing CA": "    insecure-skip-tls-verify: true",
	}

	for name, cluster := range cases {
		t.Run(name, func(t *testing.T) {
			_, _, err := loadFrom(t, fmt.Sprintf(template, cluster), nil)
			if errors.Is(err, ErrInsecureKubeconfig) == false {
				t.Errorf("err = %v, want ErrInsecureKubeconfig", err)
			}
		})
	}

	for name, cluster := range map[string]string{
		"false":        ca + "    insecure-skip-tls-verify: false",
		"quoted false": ca + `    insecure-skip-tls-verify: "false"`,
		"null":         ca + "    insecure-skip-tls-verify:",
		"absent":       ca,
	} {
		t.Run("allowed "+name, func(t *testing.T) {
			if _, _, err := loadFrom(t, fmt.Sprintf(template, cluster), nil); err != nil {
				t.Errorf("err = %v, want success", err)
			}
		})
	}
}

// Intent: without a certificate authority the server cannot be verified, so loading fails with ErrNoCA rather than
// falling back to the system roots.
func TestLoadKubeconfigRequiresCA(t *testing.T) {
	template := `current-context: c
clusters:
- name: k
  cluster:
    server: https://k:6443
%s
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`

	cases := map[string]string{
		"neither":         "",
		"empty data":      `    certificate-authority-data: ""`,
		"whitespace data": `    certificate-authority-data: "  "`,
		"empty file":      "    certificate-authority: empty.crt",
	}

	for name, cluster := range cases {
		t.Run(name, func(t *testing.T) {
			_, _, err := loadFrom(t, fmt.Sprintf(template, cluster), map[string][]byte{kubeconfigDir + "/empty.crt": {}})
			if errors.Is(err, ErrNoCA) == false {
				t.Errorf("err = %v, want ErrNoCA", err)
			}
		})
	}

	t.Run("unreadable CA file is a read error, not ErrNoCA", func(t *testing.T) {
		_, _, err := loadFrom(t, fmt.Sprintf(template, "    certificate-authority: gone.crt"), nil)
		if (err == nil) || errors.Is(err, ErrNoCA) {
			t.Errorf("err = %v, want a file error", err)
		}
	})
}

func TestLoadKubeconfigRequiresServer(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`, b64("CA"))

	if _, _, err := loadFrom(t, kubeconfig, nil); err == nil {
		t.Errorf("a cluster without a server must fail")
	}
}

// Intent: a user with no credentials is not an error here (the context may simply name no user); the cluster
// settings still load.
func TestLoadKubeconfigContextWithoutUser(t *testing.T) {
	kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k}
`, b64("CA"))

	cfg, _ := mustLoad(t, kubeconfig, nil)

	if cfg.Token != "" || len(cfg.ClientCert) != 0 || len(cfg.ClientKey) != 0 {
		t.Errorf("Config = %v, want no credentials", cfg)
	}
}

func TestLoadKubeconfigRejectsBadBase64(t *testing.T) {
	for _, field := range []string{
		"certificate-authority-data",
	} {
		kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", %s: "!!!not-base64!!!"}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`, field)

		if _, _, err := loadFrom(t, kubeconfig, nil); err == nil {
			t.Errorf("%s: invalid base64 must fail", field)
		}
	}

	for _, field := range []string{"client-certificate-data", "client-key-data"} {
		kubeconfig := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {%s: "!!!not-base64!!!"}
`, b64("CA"), field)

		if _, _, err := loadFrom(t, kubeconfig, nil); err == nil {
			t.Errorf("%s: invalid base64 must fail", field)
		}
	}
}

// Intent: an error about a malformed kubeconfig never quotes the file, because a kubeconfig scalar can be a
// credential and the YAML decoder echoes the scalar it failed on.
func TestLoadKubeconfigErrorsDoNotEchoCredentials(t *testing.T) {
	const secret = "s3cr3t-token-value-xyz"

	inputs := []string{
		fmt.Sprintf("users: [%q]", secret),
		fmt.Sprintf("current-context: [%s]", secret),
		fmt.Sprintf("clusters:\n- name: a\n  cluster: %s\n", secret),
		fmt.Sprintf("users:\n- name: u\n  user:\n    token: [%s, {\n", secret),
		fmt.Sprintf("contexts: {name: %s}", secret),
	}

	for _, input := range inputs {
		_, _, err := loadFrom(t, input, nil)
		if err == nil {
			t.Errorf("input %q: expected an error", input)

			continue
		}

		if strings.Contains(err.Error(), secret) {
			t.Errorf("input %q: error %q echoes the file content", input, err)
		}
	}
}

// Intent: the Config never prints a secret, whichever verb formats it.
func TestConfigFormattingRedactsCredentials(t *testing.T) {
	cfg := Config{
		Server:     "https://user:urlpassword@k:6443",
		CAData:     []byte("CA-BYTES"),
		ClientCert: []byte("CERT-BYTES"),
		ClientKey:  []byte("KEY-BYTES-SECRET"),
		Token:      "TOKEN-SECRET",
	}

	for _, format := range []string{"%v", "%+v", "%#v", "%s"} {
		for name, value := range map[string]any{"value": cfg, "pointer": &cfg} {
			text := fmt.Sprintf(format, value)

			for _, secret := range []string{"TOKEN-SECRET", "KEY-BYTES-SECRET", "urlpassword", "CERT-BYTES", "CA-BYTES"} {
				if strings.Contains(text, secret) {
					t.Errorf("%s of a Config %s leaks %q: %s", format, name, secret, text)
				}
			}
		}
	}

	if strings.Contains(fmt.Sprintf("%v", cfg), "k:6443") == false {
		t.Errorf("the redacted form should still name the server host: %v", cfg)
	}
}

// Intent: structured logging redacts a Config too; the JSON handler would otherwise encode every exported field.
func TestConfigLogValueRedactsCredentials(t *testing.T) {
	cfg := Config{Server: "https://k:6443", ClientKey: []byte("KEY-BYTES-SECRET"), Token: "TOKEN-SECRET"}

	for name, newHandler := range map[string]func(io.Writer) slog.Handler{
		"json": func(w io.Writer) slog.Handler { return slog.NewJSONHandler(w, nil) },
		"text": func(w io.Writer) slog.Handler { return slog.NewTextHandler(w, nil) },
	} {
		var out bytes.Buffer

		slog.New(newHandler(&out)).Info("loaded", "config", cfg, "pointer", &cfg)

		for _, secret := range []string{"TOKEN-SECRET", "KEY-BYTES-SECRET", "S0VZLUJZVEVTLVNFQ1JFVA"} {
			if strings.Contains(out.String(), secret) {
				t.Errorf("%s handler leaks %q: %s", name, secret, out.String())
			}
		}

		if strings.Contains(out.String(), "k:6443") == false {
			t.Errorf("%s handler output should name the server: %s", name, out.String())
		}
	}
}

// Intent: with no reader supplied, LoadKubeconfig reads the real filesystem, resolving relative files against the
// kubeconfig's directory.
func TestLoadKubeconfigDefaultReader(t *testing.T) {
	dir := t.TempDir()

	if err := os.WriteFile(filepath.Join(dir, "ca.crt"), []byte("DISK-CA"), 0o600); err != nil {
		t.Fatal(err)
	}

	kubeconfig := `current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority: ca.crt}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: disk-token}
`

	path := filepath.Join(dir, "admin.conf")
	if err := os.WriteFile(path, []byte(kubeconfig), 0o600); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadKubeconfig(path, nil)
	if err != nil {
		t.Fatalf("LoadKubeconfig: %v", err)
	}

	if string(cfg.CAData) != "DISK-CA" || cfg.Token != "disk-token" {
		t.Errorf("Config = %v, want the CA read from disk", cfg)
	}

	if _, err := LoadKubeconfig(filepath.Join(dir, "missing"), nil); err == nil {
		t.Errorf("a missing kubeconfig must fail")
	}
}

// Intent: arbitrary file content never crashes the agent: every bad input yields an error, and a success always
// carries a server and a CA.
func TestLoadKubeconfigNoPanic(t *testing.T) {
	valid := fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: "https://k:6443", certificate-authority-data: %s}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {token: t}
`, b64("CA"))

	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"clusters:",               // key with no value
		"clusters: 5",             // wrong type for a list
		"clusters:\n- cluster: 5", // wrong type for the nested object
		"clusters:\n- name: a\n  cluster:\n    server: [1, 2]",                // wrong type for a string
		"users:\n- user:\n    client-certificate-data: !!!",                   // invalid base64
		"contexts:\n- name: a\n  context: null\ncurrent-context: a",           // null context body
		"current-context: a\ncontexts: [{name: a}]",                           // context without cluster or user
		"- just\n- a\n- list",                                                 // document is a sequence
		"just a scalar",                                                       // document is a scalar
		"clusters:\n- name: a\n  name: b",                                     // duplicate key
		"a: &a [*a]",                                                          // recursive alias
		"a: &a [*a, *a]\nb: &b [*a, *a]\nc: &c [*b, *b]\nd: [*c, *c, *c, *c]", // alias expansion
		truncatedInput(valid),                                                 // only the first line
		valid[:len(valid)/2],                                                  // cut off mid-document
		valid[:len(valid)/3],                                                  // cut off earlier
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("LoadKubeconfig panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()

			files := &fakeFiles{files: map[string][]byte{kubeconfigPath: []byte(input)}}

			cfg, err := LoadKubeconfig(kubeconfigPath, files.read)
			if err != nil {
				return
			}

			if (cfg.Server == "") || (len(cfg.CAData) == 0) {
				t.Errorf("LoadKubeconfig succeeded without a server or CA for input %q: %v", input[:min(len(input), 50)], cfg)
			}
		})
	}
}
