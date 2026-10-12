// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type leaseBody struct {
	Kind string `json:"kind"`
	Name string `json:"name"`
}

// Intent: a bearer token is sent in the Authorization header, the request is a JSON GET, and the answer decodes
// into out.
func TestDoSendsBearerToken(t *testing.T) {
	var got *http.Request

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r
		writeJSON(w, http.StatusOK, leaseBody{Kind: "Lease", Name: "vord-reporter"})
	}))

	cfg := configFor(server)
	cfg.Token = "the-token"
	client := newTestClient(t, cfg)

	var out leaseBody
	if err := client.Do(context.Background(), http.MethodGet, "/apis/coordination.k8s.io/v1/namespaces/vord-system/leases/vord-reporter", nil, nil, &out); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if out.Kind != "Lease" || out.Name != "vord-reporter" {
		t.Errorf("out = %+v", out)
	}

	if got.Header.Get("Authorization") != "Bearer the-token" {
		t.Errorf("Authorization = %q", got.Header.Get("Authorization"))
	}

	if got.Header.Get("Accept") != "application/json" {
		t.Errorf("Accept = %q", got.Header.Get("Accept"))
	}

	if got.Method != http.MethodGet || got.URL.Path != "/apis/coordination.k8s.io/v1/namespaces/vord-system/leases/vord-reporter" {
		t.Errorf("request = %s %s", got.Method, got.URL.Path)
	}
}

// Intent: a client certificate authenticates the connection itself, and no Authorization header is sent for it.
func TestDoSendsClientCertificate(t *testing.T) {
	pair := newTestKeyPair(t, "admin-user")

	clientCAs := x509.NewCertPool()
	clientCAs.AddCert(pair.cert)

	var commonName, authorization atomic.Value

	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		commonName.Store(r.TLS.PeerCertificates[0].Subject.CommonName)
		authorization.Store(r.Header.Get("Authorization"))
		writeJSON(w, http.StatusOK, leaseBody{Kind: "Lease"})
	}))
	server.Config.ErrorLog = log.New(io.Discard, "", 0)
	server.TLS = &tls.Config{ClientAuth: tls.RequireAndVerifyClientCert, ClientCAs: clientCAs}
	server.StartTLS()
	t.Cleanup(server.Close)

	cfg := configFor(server)
	cfg.ClientCert = pair.certPEM
	cfg.ClientKey = pair.keyPEM
	client := newTestClient(t, cfg)

	if err := client.Do(context.Background(), http.MethodGet, "/version", nil, nil, nil); err != nil {
		t.Fatalf("Do with a client certificate: %v", err)
	}

	if commonName.Load() != "admin-user" {
		t.Errorf("server saw client certificate %v, want admin-user", commonName.Load())
	}

	if authorization.Load() != "" {
		t.Errorf("Authorization = %q, want none", authorization.Load())
	}

	withoutCert := newTestClient(t, configFor(server))

	err := withoutCert.Do(context.Background(), http.MethodGet, "/version", nil, nil, nil)
	if err == nil {
		t.Fatalf("a server that requires a client certificate accepted a client without one")
	}

	if IsTLSVerify(err) || IsAuthRejected(err) {
		t.Errorf("a missing client certificate is neither a verification failure nor an API rejection: %v", err)
	}
}

// Intent: a kubeconfig that names a token file, loaded and used end to end, sends the file's token.
func TestKubeconfigWithTokenFileEndToEnd(t *testing.T) {
	var authorization atomic.Value

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		authorization.Store(r.Header.Get("Authorization"))
		writeJSON(w, http.StatusOK, leaseBody{Kind: "Lease"})
	}))

	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "ca.crt"), serverCAPEM(server))
	writeFile(t, filepath.Join(dir, "token"), []byte("projected-token\n"))
	writeFile(t, filepath.Join(dir, "config"), []byte(fmt.Sprintf(`current-context: c
clusters:
- name: k
  cluster: {server: %q, certificate-authority: ca.crt}
contexts:
- name: c
  context: {cluster: k, user: u}
users:
- name: u
  user: {tokenFile: token}
`, server.URL)))

	cfg, err := LoadKubeconfig(filepath.Join(dir, "config"), os.ReadFile)
	if err != nil {
		t.Fatalf("LoadKubeconfig: %v", err)
	}

	client := newTestClient(t, cfg)

	if err := client.Do(context.Background(), http.MethodGet, "/version", nil, nil, nil); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if authorization.Load() != "Bearer projected-token" {
		t.Errorf("Authorization = %v, want the trimmed file token", authorization.Load())
	}
}

func writeFile(t *testing.T, path string, data []byte) {
	t.Helper()

	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
}

// Intent: a server whose certificate the kubeconfig CA does not vouch for is refused, and the failure is reported
// as a TLS verification failure, not as an API error. The system roots play no part.
func TestDoRefusesServerSignedByAnotherCA(t *testing.T) {
	var served atomic.Int32

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		served.Add(1)
		writeJSON(w, http.StatusOK, leaseBody{})
	}))

	cfg := Config{Server: server.URL, CAData: newTestKeyPair(t, "some other CA").certPEM, Token: "must-not-be-sent"}
	client := newTestClient(t, cfg)

	err := client.Do(context.Background(), http.MethodGet, "/version", nil, nil, nil)
	if err == nil {
		t.Fatalf("expected a verification failure")
	}

	if IsTLSVerify(err) == false {
		t.Errorf("IsTLSVerify = false for %v", err)
	}

	if apiErrorOf(err) != nil {
		t.Errorf("a verification failure must not look like an API error: %v", err)
	}

	if served.Load() != 0 {
		t.Errorf("the request reached the server %d times despite the failed verification", served.Load())
	}

	if strings.Contains(err.Error(), "must-not-be-sent") {
		t.Errorf("the error leaks the token: %v", err)
	}
}

// Intent: tls-server-name is honoured. The certificate is checked against that name even though the connection
// goes to an IP address, and a name the certificate does not cover is a verification failure.
func TestDoHonoursTLSServerName(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, leaseBody{})
	}))

	covered := configFor(server)
	covered.TLSServerName = "example.com"

	if err := newTestClient(t, covered).Do(context.Background(), http.MethodGet, "/version", nil, nil, nil); err != nil {
		t.Errorf("a name the certificate covers must verify: %v", err)
	}

	uncovered := configFor(server)
	uncovered.TLSServerName = "not-in-the-certificate.example.net"

	err := newTestClient(t, uncovered).Do(context.Background(), http.MethodGet, "/version", nil, nil, nil)
	if IsTLSVerify(err) == false {
		t.Errorf("err = %v, want a TLS verification failure for a name the certificate does not cover", err)
	}
}

// Intent: the transport can never be loosened by accident: it verifies, trusts only the supplied CA, ignores
// proxy settings in the environment and requires a modern TLS version.
func TestNewHardensTransport(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	client := newTestClient(t, configFor(server))

	transport, ok := client.http.Transport.(*http.Transport)
	if ok == false {
		t.Fatalf("transport is %T, want *http.Transport", client.http.Transport)
	}

	if transport.Proxy != nil {
		t.Errorf("Proxy is set; credentials must never travel through a proxy from the environment")
	}

	tlsConfig := transport.TLSClientConfig
	if tlsConfig == nil {
		t.Fatalf("TLSClientConfig is nil")
	}

	if tlsConfig.InsecureSkipVerify {
		t.Errorf("InsecureSkipVerify is set")
	}

	if tlsConfig.RootCAs == nil {
		t.Errorf("RootCAs is nil, which would fall back to the system roots")
	}

	if tlsConfig.MinVersion < tls.VersionTLS12 {
		t.Errorf("MinVersion = %#x, want at least TLS 1.2", tlsConfig.MinVersion)
	}

	if tlsConfig.VerifyConnection != nil || tlsConfig.VerifyPeerCertificate != nil {
		t.Errorf("custom verification callbacks must not replace the default verification")
	}
}

// Intent: redirects are never followed, so a bearer token cannot be forwarded to another host; the redirect
// itself is reported as an error carrying its status code.
func TestDoDoesNotFollowRedirects(t *testing.T) {
	var targetHits atomic.Int32

	target := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		targetHits.Add(1)
		writeJSON(w, http.StatusOK, leaseBody{})
	}))

	source := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, target.URL+"/stolen", http.StatusTemporaryRedirect)
	}))

	cfg := configFor(source)
	cfg.Token = "tok"

	err := newTestClient(t, cfg).Do(context.Background(), http.MethodGet, "/version", nil, nil, nil)

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusTemporaryRedirect) {
		t.Errorf("err = %v, want an APIError with code 307", err)
	}

	if targetHits.Load() != 0 {
		t.Errorf("the redirect target was contacted %d times", targetHits.Load())
	}
}

// Intent: New refuses a configuration that would weaken or break the connection.
func TestNewValidatesConfig(t *testing.T) {
	good := newTestKeyPair(t, "ca")
	other := newTestKeyPair(t, "client")

	cases := []struct {
		name    string
		cfg     Config
		wantErr error
		leaks   string
	}{
		{"plain http", Config{Server: "http://k:6443", CAData: good.certPEM}, nil, ""},
		{"no scheme", Config{Server: "k:6443", CAData: good.certPEM}, nil, ""},
		{"empty server", Config{CAData: good.certPEM}, nil, ""},
		{"server with credentials", Config{Server: "https://admin:pa55w0rd-leak@k:6443", CAData: good.certPEM}, nil, "pa55w0rd-leak"},
		{"server with a query", Config{Server: "https://k:6443/?x=1", CAData: good.certPEM}, nil, ""},
		{"unparsable server", Config{Server: "https://k:6443\x7f", CAData: good.certPEM}, nil, ""},
		{"no CA", Config{Server: "https://k:6443"}, ErrNoCA, ""},
		{"CA without a certificate", Config{Server: "https://k:6443", CAData: []byte("not pem")}, nil, ""},
		{"CA that is a private key", Config{Server: "https://k:6443", CAData: good.keyPEM}, nil, ""},
		{"certificate without a key", Config{Server: "https://k:6443", CAData: good.certPEM, ClientCert: other.certPEM}, nil, ""},
		{"key without a certificate", Config{Server: "https://k:6443", CAData: good.certPEM, ClientKey: other.keyPEM}, nil, ""},
		{"mismatched certificate and key", Config{Server: "https://k:6443", CAData: good.certPEM, ClientCert: other.certPEM, ClientKey: good.keyPEM}, nil, ""},
		{"token with a newline", Config{Server: "https://k:6443", CAData: good.certPEM, Token: "abc\ndef-leak"}, nil, "def-leak"},
		{"token with a carriage return", Config{Server: "https://k:6443", CAData: good.certPEM, Token: "abc\r"}, nil, ""},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			client, err := New(tc.cfg)
			if err == nil {
				t.Fatalf("New succeeded: %v", client)
			}

			if (tc.wantErr != nil) && (errors.Is(err, tc.wantErr) == false) {
				t.Errorf("err = %v, want %v", err, tc.wantErr)
			}

			if (tc.leaks != "") && strings.Contains(err.Error(), tc.leaks) {
				t.Errorf("error %q leaks %q", err, tc.leaks)
			}
		})
	}

	valid := []Config{
		{Server: "https://k:6443", CAData: good.certPEM},
		{Server: "https://k:6443/", CAData: good.certPEM, Token: "t"},
		{Server: "https://k.example/k8s/clusters/c-1", CAData: good.certPEM},
		{Server: "https://k:6443", CAData: append(append([]byte{}, other.certPEM...), good.certPEM...), ClientCert: other.certPEM, ClientKey: other.keyPEM},
	}

	for _, cfg := range valid {
		if _, err := New(cfg); err != nil {
			t.Errorf("New(%v) = %v", cfg.Server, err)
		}
	}
}

// Intent: the client never prints its token, whichever verb formats it.
func TestClientFormattingRedactsToken(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))

	cfg := configFor(server)
	cfg.Token = "TOKEN-SECRET"
	client := newTestClient(t, cfg)

	for _, format := range []string{"%v", "%+v", "%#v", "%s"} {
		if text := fmt.Sprintf(format, client); strings.Contains(text, "TOKEN-SECRET") {
			t.Errorf("%s of a Client leaks the token: %s", format, text)
		}
	}
}

// Intent: the request body is JSON, the method and query are passed on, and a server mounted below a path prefix
// keeps its prefix.
func TestDoRequestShape(t *testing.T) {
	var method, rawQuery, path, contentType, body string

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		method, rawQuery, path, contentType = r.Method, r.URL.RawQuery, r.URL.Path, r.Header.Get("Content-Type")

		data, _ := io.ReadAll(r.Body)
		body = string(data)

		writeJSON(w, http.StatusCreated, leaseBody{Kind: "Created"})
	}))

	cfg := configFor(server)
	cfg.Server += "/k8s/clusters/c-1/"
	client := newTestClient(t, cfg)

	var out leaseBody

	query := url.Values{"fieldSelector": {"spec.nodeName=a b"}, "limit": {"5"}}
	if err := client.Do(context.Background(), http.MethodPost, "/api/v1/namespaces", query, map[string]any{"metadata": map[string]any{"name": "vord-system"}}, &out); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if method != http.MethodPost || path != "/k8s/clusters/c-1/api/v1/namespaces" {
		t.Errorf("request = %s %s", method, path)
	}

	if rawQuery != "fieldSelector=spec.nodeName%3Da+b&limit=5" {
		t.Errorf("query = %q", rawQuery)
	}

	if contentType != "application/json" {
		t.Errorf("Content-Type = %q", contentType)
	}

	if body != `{"metadata":{"name":"vord-system"}}` {
		t.Errorf("body = %q", body)
	}

	if out.Kind != "Created" {
		t.Errorf("out = %+v", out)
	}
}

func TestDoEmptyAndAbsentBodies(t *testing.T) {
	var hasBody atomic.Bool

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		data, _ := io.ReadAll(r.Body)
		hasBody.Store(len(data) > 0)

		w.WriteHeader(http.StatusNoContent)
	}))

	client := newTestClient(t, configFor(server))

	out := leaseBody{Kind: "untouched"}
	if err := client.Do(context.Background(), http.MethodDelete, "/api/v1/x", nil, nil, &out); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if out.Kind != "untouched" {
		t.Errorf("an empty response must leave out alone, got %+v", out)
	}

	if hasBody.Load() {
		t.Errorf("a nil body must send no body")
	}
}

func TestDoRejectsPathWithoutLeadingSlash(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		t.Error("the request must not be sent")
	}))

	for _, path := range []string{"", "api/v1", "https://elsewhere.example/api"} {
		if err := newTestClient(t, configFor(server)).Do(context.Background(), http.MethodGet, path, nil, nil, nil); err == nil {
			t.Errorf("path %q was accepted", path)
		}
	}
}

func TestDoBadResponseBody(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte("this is not json"))
	}))

	var out leaseBody

	err := newTestClient(t, configFor(server)).Do(context.Background(), http.MethodGet, "/x", nil, nil, &out)
	if err == nil {
		t.Errorf("an undecodable success body must be an error")
	}

	if apiErrorOf(err) != nil {
		t.Errorf("an undecodable success body is not an API error: %v", err)
	}
}

// Intent: error answers become APIErrors with the code and the Status reason, including the non-JSON 401 a proxy
// or an old server can send.
func TestDoReturnsAPIErrors(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/exists":
			writeStatus(w, http.StatusConflict, "AlreadyExists", "leases \"vord-reporter\" already exists")
		case "/conflict":
			writeStatus(w, http.StatusConflict, "Conflict", "the object has been modified; please apply your changes to the latest version")
		case "/missing":
			writeStatus(w, http.StatusNotFound, "NotFound", "leases \"vord-reporter\" not found")
		case "/forbidden":
			writeStatus(w, http.StatusForbidden, "Forbidden", "leases is forbidden")
		case "/plain401":
			w.Header().Set("Content-Type", "text/html")
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte("<html>401 Authorization Required</html>"))
		case "/empty500":
			w.WriteHeader(http.StatusInternalServerError)
		default:
			t.Errorf("unexpected path %s", r.URL.Path)
		}
	}))

	client := newTestClient(t, configFor(server))

	call := func(path string) error {
		return client.Do(context.Background(), http.MethodGet, path, nil, nil, nil)
	}

	if err := call("/exists"); IsAlreadyExists(err) == false || IsConflict(err) {
		t.Errorf("409 AlreadyExists: err = %v", err)
	}

	if err := call("/conflict"); IsConflict(err) == false || IsAlreadyExists(err) {
		t.Errorf("409 Conflict: err = %v", err)
	}

	if err := call("/missing"); IsNotFound(err) == false {
		t.Errorf("404: err = %v", err)
	}

	if err := call("/forbidden"); IsAuthRejected(err) == false {
		t.Errorf("403: err = %v", err)
	}

	err := call("/plain401")

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusUnauthorized) || (apiError.Reason != "") {
		t.Fatalf("non-JSON 401: err = %v", err)
	}

	if IsAuthRejected(err) == false {
		t.Errorf("a non-JSON 401 must satisfy IsAuthRejected")
	}

	if apiError.Message != "<html>401 Authorization Required</html>" {
		t.Errorf("Message = %q", apiError.Message)
	}

	err = call("/empty500")
	if apiError := apiErrorOf(err); (apiError == nil) || (apiError.Code != http.StatusInternalServerError) {
		t.Errorf("empty 500: err = %v", err)
	}
}

// Intent: a response larger than the cap is refused instead of being buffered, whether it is a success or an
// error body, and an oversized error body still reports its status code.
func TestDoCapsResponseBody(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		filler := strings.Repeat("x", 4096)

		if r.URL.Path == "/error" {
			w.WriteHeader(http.StatusBadGateway)
		}

		_, _ = w.Write([]byte(`{"filler":"` + filler + `"}`))
	}))

	client := newTestClient(t, configFor(server))
	client.maxBody = 1024

	var out map[string]any

	err := client.Do(context.Background(), http.MethodGet, "/ok", nil, nil, &out)
	if err == nil {
		t.Errorf("an oversized success body must be refused")
	}

	if (err != nil) && (strings.Contains(err.Error(), "1024") == false) {
		t.Errorf("error %q should name the limit", err)
	}

	err = client.Do(context.Background(), http.MethodGet, "/error", nil, nil, nil)

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusBadGateway) {
		t.Errorf("an oversized error body must still give an APIError with its code, got %v", err)
	}

	if (apiError != nil) && (len(apiError.Message) > 1024+len("...")) {
		t.Errorf("Message is %d bytes", len(apiError.Message))
	}

	if newTestClient(t, configFor(server)).maxBody != 32<<20 {
		t.Errorf("the default response cap is not 32 MiB")
	}
}

// endlessBody is a response body that never ends and counts what is read from it.
type endlessBody struct {
	read int64
}

func (b *endlessBody) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = 'x'
	}

	b.read += int64(len(p))

	return len(p), nil
}

func (b *endlessBody) Close() error { return nil }

// Intent: an error answer is read only as far as a Status object could need, however large the success cap is.
func TestDoReadsBoundedErrorBody(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	client := newTestClient(t, configFor(server))

	body := &endlessBody{}
	client.http.Transport = roundTripperFunc(func(req *http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: http.StatusInternalServerError, Header: http.Header{}, Body: body, Request: req}, nil
	})

	err := client.Do(context.Background(), http.MethodGet, "/x", nil, nil, nil)

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusInternalServerError) {
		t.Fatalf("err = %v, want an APIError with code 500", err)
	}

	if body.read > maxErrorBodyBytes+1 {
		t.Errorf("read %d bytes of an error body, want at most %d", body.read, maxErrorBodyBytes+1)
	}
}

// roundTripperFunc adapts a function to http.RoundTripper.
type roundTripperFunc func(*http.Request) (*http.Response, error)

func (f roundTripperFunc) RoundTrip(req *http.Request) (*http.Response, error) { return f(req) }

// deadlineRecorder is a transport that records the deadline of the context each request carries and answers with
// an empty object.
type deadlineRecorder struct {
	deadlines []time.Time
}

func (d *deadlineRecorder) RoundTrip(req *http.Request) (*http.Response, error) {
	deadline, _ := req.Context().Deadline()
	d.deadlines = append(d.deadlines, deadline)

	return &http.Response{
		StatusCode: http.StatusOK,
		Header:     http.Header{},
		Body:       io.NopCloser(strings.NewReader("{}")),
		Request:    req,
	}, nil
}

// Intent: every request carries a 30 second deadline set through its context, and an earlier caller deadline is
// kept.
func TestDoAppliesRequestTimeout(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	client := newTestClient(t, configFor(server))

	recorder := &deadlineRecorder{}
	client.http.Transport = recorder

	before := time.Now()

	if err := client.Do(context.Background(), http.MethodGet, "/x", nil, nil, nil); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if len(recorder.deadlines) != 1 || recorder.deadlines[0].IsZero() {
		t.Fatalf("the request carried no deadline: %v", recorder.deadlines)
	}

	if span := recorder.deadlines[0].Sub(before); (span < 30*time.Second) || (span > 40*time.Second) {
		t.Errorf("deadline is %v after the call began, want 30s", span)
	}

	soon, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if err := client.Do(soon, http.MethodGet, "/x", nil, nil, nil); err != nil {
		t.Fatalf("Do: %v", err)
	}

	if span := recorder.deadlines[1].Sub(before); span > 10*time.Second {
		t.Errorf("an earlier caller deadline was extended to %v", span)
	}
}

func TestDoHonoursCancelledContext(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("a cancelled request must not reach the server")
	}))

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	err := newTestClient(t, configFor(server)).Do(ctx, http.MethodGet, "/x", nil, nil, nil)
	if errors.Is(err, context.Canceled) == false {
		t.Errorf("err = %v, want context.Canceled", err)
	}
}
