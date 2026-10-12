// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"io"
	"log"
	"math/big"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

// serverCAPEM returns the certificate a test server presents as PEM, which is also the CA that verifies it.
func serverCAPEM(server *httptest.Server) []byte {
	return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: server.Certificate().Raw})
}

// newTLSServer starts a TLS test server that closes with the test. Its error log is discarded, so handshakes the
// tests refuse on purpose leave no noise.
func newTLSServer(t *testing.T, handler http.Handler) *httptest.Server {
	t.Helper()

	server := httptest.NewUnstartedServer(handler)
	server.Config.ErrorLog = log.New(io.Discard, "", 0)
	server.StartTLS()
	t.Cleanup(server.Close)

	return server
}

// configFor returns a Config that trusts server's certificate and nothing else.
func configFor(server *httptest.Server) Config {
	return Config{Server: server.URL, CAData: serverCAPEM(server)}
}

// newTestClient builds a Client for cfg and fails the test when that is refused.
func newTestClient(t *testing.T, cfg Config) *Client {
	t.Helper()

	client, err := New(cfg)
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	return client
}

// writeJSON answers with a JSON body.
func writeJSON(w http.ResponseWriter, code int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)

	if err := json.NewEncoder(w).Encode(value); err != nil {
		panic(err)
	}
}

// writeStatus answers with a Kubernetes Status failure object.
func writeStatus(w http.ResponseWriter, code int, reason string, message string) {
	writeJSON(w, code, map[string]any{
		"kind":       "Status",
		"apiVersion": "v1",
		"status":     "Failure",
		"message":    message,
		"reason":     reason,
		"code":       code,
	})
}

// testKeyPair is a self-signed certificate with its key, usable as a client certificate or as a CA that matches
// no test server.
type testKeyPair struct {
	cert    *x509.Certificate
	certPEM []byte
	keyPEM  []byte
}

// newTestKeyPair builds a self-signed ECDSA certificate with fixed validity dates, so the tests never depend on
// the wall clock.
func newTestKeyPair(t *testing.T, commonName string) testKeyPair {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: commonName},
		NotBefore:             time.Date(2026, time.January, 1, 0, 0, 0, 0, time.UTC),
		NotAfter:              time.Date(2036, time.January, 1, 0, 0, 0, 0, time.UTC),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
		BasicConstraintsValid: true,
		IsCA:                  true,
	}

	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatalf("create certificate: %v", err)
	}

	parsed, err := x509.ParseCertificate(der)
	if err != nil {
		t.Fatalf("parse certificate: %v", err)
	}

	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatalf("marshal key: %v", err)
	}

	return testKeyPair{
		cert:    parsed,
		certPEM: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		keyPEM:  pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}),
	}
}
