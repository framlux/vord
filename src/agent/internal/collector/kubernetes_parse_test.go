// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"bytes"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"fmt"
	"math/big"
	"strings"
	"testing"
	"time"
)

// testCert is a self-signed certificate built in the test, with the key material needed to
// write it as the PEM blocks a Kubernetes node keeps on disk.
type testCert struct {
	cert    *x509.Certificate
	certPEM []byte
	keyPEM  []byte
}

// newTestCert builds a self-signed ECDSA certificate with the given common name. Validity uses
// fixed dates so the output never depends on the wall clock.
func newTestCert(t *testing.T, commonName string, serial int64) testCert {
	t.Helper()

	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("generate key: %v", err)
	}

	template := &x509.Certificate{
		SerialNumber: big.NewInt(serial),
		Subject:      pkix.Name{CommonName: commonName},
		NotBefore:    time.Date(2026, time.January, 1, 0, 0, 0, 0, time.UTC),
		NotAfter:     time.Date(2036, time.January, 1, 0, 0, 0, 0, time.UTC),
		KeyUsage:     x509.KeyUsageDigitalSignature,
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

	return testCert{
		cert:    parsed,
		certPEM: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		keyPEM:  pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}),
	}
}

func sha256Hex(data []byte) string {
	sum := sha256.Sum256(data)

	return hex.EncodeToString(sum[:])
}

// --- caFingerprint ---

// Intent: the fingerprint is the lowercase hex SHA-256 of the DER bytes of the first certificate,
// so two nodes of one cluster agree on it however many certificates follow in the bundle.
func TestCaFingerprintUsesFirstCertificateOfBundle(t *testing.T) {
	first := newTestCert(t, "kubernetes-ca", 1)
	second := newTestCert(t, "front-proxy-ca", 2)

	bundle := append(append([]byte{}, first.certPEM...), second.certPEM...)

	got, err := caFingerprint(bundle)
	if err != nil {
		t.Fatalf("caFingerprint returned error: %v", err)
	}

	want := sha256Hex(first.cert.Raw)
	if got != want {
		t.Errorf("caFingerprint = %q, want %q", got, want)
	}

	if got != strings.ToLower(got) {
		t.Errorf("caFingerprint = %q, want lowercase hex", got)
	}

	if got == sha256Hex(second.cert.Raw) {
		t.Errorf("caFingerprint matched the second certificate of the bundle")
	}
}

// Intent: a private key is never a CA: a PEM file holding only a key yields an error, so a
// misconfigured path cannot produce a fingerprint from key material.
func TestCaFingerprintRejectsFileWithOnlyPrivateKey(t *testing.T) {
	c := newTestCert(t, "kubernetes-ca", 1)

	got, err := caFingerprint(c.keyPEM)
	if err == nil {
		t.Errorf("caFingerprint accepted a key-only file and returned %q", got)
	}

	if got != "" {
		t.Errorf("caFingerprint = %q on error, want empty", got)
	}
}

// Intent: a key block ahead of the certificate is walked past without being decoded, and the
// fingerprint is still taken from the certificate.
func TestCaFingerprintSkipsPrivateKeyBlockBeforeCertificate(t *testing.T) {
	c := newTestCert(t, "kubernetes-ca", 1)

	garbageKey := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: []byte("not a der structure")})
	input := append(append([]byte{}, garbageKey...), c.certPEM...)

	got, err := caFingerprint(input)
	if err != nil {
		t.Fatalf("caFingerprint returned error: %v", err)
	}

	if want := sha256Hex(c.cert.Raw); got != want {
		t.Errorf("caFingerprint = %q, want %q", got, want)
	}
}

// Intent: a CERTIFICATE block whose bytes are not a certificate is an error rather than a
// fingerprint of garbage.
func TestCaFingerprintRejectsInvalidCertificateBlock(t *testing.T) {
	input := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: []byte("not a der structure")})

	got, err := caFingerprint(input)
	if err == nil {
		t.Errorf("caFingerprint accepted an invalid certificate and returned %q", got)
	}
}

// Intent: hostile or truncated files on a node never crash the agent: every bad input yields an error and no fingerprint.
func TestCaFingerprintNoPanic(t *testing.T) {
	c := newTestCert(t, "kubernetes-ca", 1)

	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"-----BEGIN CERTIFICATE-----",                                  // header with no body
		"-----BEGIN CERTIFICATE-----\n!!!!\n-----END CERTIFICATE-----", // body that is not base64
		"-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----", // base64 that is not a certificate
		string(c.certPEM[:len(c.certPEM)/2]),                           // certificate cut off mid-body
		truncatedInput(string(c.certPEM)),                              // only the BEGIN line
		string(c.keyPEM),                                               // key block only
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("caFingerprint panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()

			got, err := caFingerprint([]byte(input))
			if (err == nil) && (got != "") {
				t.Errorf("caFingerprint returned %q without an error for input %q", got, input[:min(len(input), 50)])
			}
		})
	}
}

// --- nodeNameFromCert ---

// Intent: the kubelet client certificate identifies the node by its CN system:node:<name>, and
// that name is what the collector reports as the node name.
func TestNodeNameFromCertReadsNodeCommonName(t *testing.T) {
	c := newTestCert(t, "system:node:node-1", 1)

	got, err := nodeNameFromCert(c.certPEM)
	if err != nil {
		t.Fatalf("nodeNameFromCert returned error: %v", err)
	}

	if got != "node-1" {
		t.Errorf("nodeNameFromCert = %q, want %q", got, "node-1")
	}
}

// Intent: kubelet-client-current.pem combines the certificate and its key in one file, in either
// order, and the name is read in both layouts.
func TestNodeNameFromCertReadsCombinedFileWithKeyBeforeAndAfter(t *testing.T) {
	c := newTestCert(t, "system:node:worker-7", 1)

	layouts := map[string][]byte{
		"key before certificate": append(append([]byte{}, c.keyPEM...), c.certPEM...),
		"key after certificate":  append(append([]byte{}, c.certPEM...), c.keyPEM...),
	}

	for name, input := range layouts {
		t.Run(name, func(t *testing.T) {
			got, err := nodeNameFromCert(input)
			if err != nil {
				t.Fatalf("nodeNameFromCert returned error: %v", err)
			}

			if got != "worker-7" {
				t.Errorf("nodeNameFromCert = %q, want %q", got, "worker-7")
			}
		})
	}
}

// Intent: private key blocks are skipped without being decoded, so a key block that is not valid
// DER neither fails the read nor is ever parsed.
func TestNodeNameFromCertSkipsKeyBlocksWithoutDecodingThem(t *testing.T) {
	c := newTestCert(t, "system:node:node-1", 1)

	garbageKey := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: []byte("not a der structure")})
	input := append(append([]byte{}, garbageKey...), c.certPEM...)

	got, err := nodeNameFromCert(input)
	if err != nil {
		t.Fatalf("nodeNameFromCert returned error: %v", err)
	}

	if got != "node-1" {
		t.Errorf("nodeNameFromCert = %q, want %q", got, "node-1")
	}
}

// Intent: only a node certificate names a node: the admin credential's CN is rejected so an
// admin client certificate is never mistaken for a kubelet identity.
func TestNodeNameFromCertRejectsNonNodeCommonName(t *testing.T) {
	for _, commonName := range []string{"kubernetes-admin", "system:node:", "system:nodes", "node-1", ""} {
		t.Run(commonName, func(t *testing.T) {
			c := newTestCert(t, commonName, 1)

			got, err := nodeNameFromCert(c.certPEM)
			if err == nil {
				t.Errorf("nodeNameFromCert accepted CN %q and returned %q", commonName, got)
			}

			if got != "" {
				t.Errorf("nodeNameFromCert = %q on error, want empty", got)
			}
		})
	}
}

// Intent: a file holding only a private key has no node identity, and the key is never read to
// find one.
func TestNodeNameFromCertRejectsFileWithOnlyKeyBlock(t *testing.T) {
	c := newTestCert(t, "system:node:node-1", 1)

	got, err := nodeNameFromCert(c.keyPEM)
	if err == nil {
		t.Errorf("nodeNameFromCert accepted a key-only file and returned %q", got)
	}

	if got != "" {
		t.Errorf("nodeNameFromCert = %q on error, want empty", got)
	}
}

// Intent: hostile or truncated certificate files never crash the agent: every bad input yields an error and no name.
func TestNodeNameFromCertNoPanic(t *testing.T) {
	c := newTestCert(t, "system:node:node-1", 1)

	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"-----BEGIN CERTIFICATE-----",                                  // header with no body
		"-----BEGIN CERTIFICATE-----\n!!!!\n-----END CERTIFICATE-----", // body that is not base64
		"-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----", // base64 that is not a certificate
		string(c.certPEM[:len(c.certPEM)/2]),                           // certificate cut off mid-body
		truncatedInput(string(c.certPEM)),                              // only the BEGIN line
		string(c.keyPEM),                                               // key block only
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("nodeNameFromCert panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()

			got, err := nodeNameFromCert([]byte(input))
			if (err == nil) && (got != "") {
				t.Errorf("nodeNameFromCert returned %q without an error for input %q", got, input[:min(len(input), 50)])
			}
		})
	}
}

// --- parseKubeconfig ---

const kubeconfigEmbeddedTemplate = `apiVersion: v1
kind: Config
current-context: default
clusters:
- name: kubernetes
  cluster:
    server: https://10.0.0.1:6443
    certificate-authority-data: %s
contexts:
- name: default
  context:
    cluster: kubernetes
    user: kubelet
users:
- name: kubelet
  user:
    client-certificate-data: %s
    client-key-data: %s
`

func refsAreEmpty(refs kubeconfigRefs) bool {
	return (refs.caData == nil) && (refs.caPath == "") && (refs.certData == nil) && (refs.certPath == "")
}

// Intent: certificates embedded in the kubeconfig are base64 on disk and must come back as the
// original bytes, so the collector fingerprints and reads the same certificate the cluster uses.
func TestParseKubeconfigDecodesEmbeddedData(t *testing.T) {
	ca := newTestCert(t, "kubernetes-ca", 1)
	client := newTestCert(t, "system:node:node-1", 2)

	data := fmt.Sprintf(kubeconfigEmbeddedTemplate,
		base64.StdEncoding.EncodeToString(ca.certPEM),
		base64.StdEncoding.EncodeToString(client.certPEM),
		base64.StdEncoding.EncodeToString(client.keyPEM))

	refs, err := parseKubeconfig([]byte(data))
	if err != nil {
		t.Fatalf("parseKubeconfig returned error: %v", err)
	}

	if bytes.Equal(refs.caData, ca.certPEM) == false {
		t.Errorf("caData did not decode to the original certificate bytes")
	}

	if bytes.Equal(refs.certData, client.certPEM) == false {
		t.Errorf("certData did not decode to the original certificate bytes")
	}

	if (refs.caPath != "") || (refs.certPath != "") {
		t.Errorf("paths = (%q, %q), want both empty for an embedded kubeconfig", refs.caPath, refs.certPath)
	}
}

// Intent: a kubeconfig that points at files returns those paths exactly as written, because the
// collector, not the parser, decides how to resolve and read them.
func TestParseKubeconfigReturnsPathsVerbatim(t *testing.T) {
	data := `apiVersion: v1
kind: Config
current-context: default
clusters:
- name: kubernetes
  cluster:
    server: https://127.0.0.1:6443
    certificate-authority: /etc/kubernetes/pki/ca.crt
contexts:
- name: default
  context:
    cluster: kubernetes
    user: kubelet
users:
- name: kubelet
  user:
    client-certificate: pki/../kubelet-client-current.pem
    client-key: /var/lib/kubelet/pki/kubelet-client-current.pem
`

	refs, err := parseKubeconfig([]byte(data))
	if err != nil {
		t.Fatalf("parseKubeconfig returned error: %v", err)
	}

	if refs.caPath != "/etc/kubernetes/pki/ca.crt" {
		t.Errorf("caPath = %q, want %q", refs.caPath, "/etc/kubernetes/pki/ca.crt")
	}

	if refs.certPath != "pki/../kubelet-client-current.pem" {
		t.Errorf("certPath = %q, want it returned verbatim", refs.certPath)
	}

	if (refs.caData != nil) || (refs.certData != nil) {
		t.Errorf("embedded data present for a path-based kubeconfig: caData=%v certData=%v", refs.caData, refs.certData)
	}
}

// Intent: a user that authenticates by token or password has no client certificate, so the parse
// succeeds, reports no certificate reference, and still returns the cluster CA.
func TestParseKubeconfigTokenAndPasswordUsersHaveNoCertificate(t *testing.T) {
	ca := newTestCert(t, "kubernetes-ca", 1)
	caB64 := base64.StdEncoding.EncodeToString(ca.certPEM)

	users := map[string]string{
		"token":    "    token: abcdef0123456789\n",
		"password": "    username: admin\n    password: s3cret\n",
	}

	for name, userBody := range users {
		t.Run(name, func(t *testing.T) {
			data := "apiVersion: v1\nkind: Config\ncurrent-context: default\n" +
				"clusters:\n- name: kubernetes\n  cluster:\n    server: https://10.0.0.1:6443\n    certificate-authority-data: " + caB64 + "\n" +
				"contexts:\n- name: default\n  context:\n    cluster: kubernetes\n    user: admin\n" +
				"users:\n- name: admin\n  user:\n" + userBody

			refs, err := parseKubeconfig([]byte(data))
			if err != nil {
				t.Fatalf("parseKubeconfig returned error: %v", err)
			}

			if (refs.certData != nil) || (refs.certPath != "") {
				t.Errorf("certificate reference = (%v, %q), want none for a %s user", refs.certData, refs.certPath, name)
			}

			if bytes.Equal(refs.caData, ca.certPEM) == false {
				t.Errorf("caData lost for a %s user", name)
			}
		})
	}
}

// Intent: corrupt base64 is a hard error rather than a silently empty reference, so a damaged
// kubeconfig is reported instead of being treated as a host without a CA.
func TestParseKubeconfigRejectsInvalidBase64(t *testing.T) {
	valid := base64.StdEncoding.EncodeToString([]byte("some certificate bytes"))

	cases := map[string]string{
		"certificate-authority-data": fmt.Sprintf(kubeconfigEmbeddedTemplate, "***not-base64***", valid, valid),
		"client-certificate-data":    fmt.Sprintf(kubeconfigEmbeddedTemplate, valid, "***not-base64***", valid),
	}

	for field, data := range cases {
		t.Run(field, func(t *testing.T) {
			_, err := parseKubeconfig([]byte(data))
			if err == nil {
				t.Errorf("parseKubeconfig accepted invalid base64 in %s", field)
			}
		})
	}
}

// Intent: malformed YAML is an error, not an empty result that would look like a node with no
// certificate references.
func TestParseKubeconfigRejectsMalformedYaml(t *testing.T) {
	cases := []string{
		"clusters: [unterminated",
		"clusters:\n- name: a\n  cluster: {server: \"https://x",
		"current-context: [a, b\nusers: ]",
		"apiVersion: v1\n\tkind: Config",
	}

	for _, data := range cases {
		t.Run("", func(t *testing.T) {
			_, err := parseKubeconfig([]byte(data))
			if err == nil {
				t.Errorf("parseKubeconfig accepted malformed YAML %q", data)
			}
		})
	}
}

const kubeconfigTwoContexts = `apiVersion: v1
kind: Config
%s
clusters:
- name: first-cluster
  cluster:
    certificate-authority: /pki/first-ca.crt
- name: second-cluster
  cluster:
    certificate-authority: /pki/second-ca.crt
contexts:
- name: first
  context:
    cluster: first-cluster
    user: first-user
- name: second
  context:
    cluster: second-cluster
    user: second-user
users:
- name: first-user
  user:
    client-certificate: /pki/first-client.pem
- name: second-user
  user:
    client-certificate: /pki/second-client.pem
`

// Intent: current-context decides which cluster and user the node really uses, so a kubeconfig
// that lists several contexts yields the selected pair and not merely the first entries.
func TestParseKubeconfigFollowsCurrentContext(t *testing.T) {
	refs, err := parseKubeconfig([]byte(fmt.Sprintf(kubeconfigTwoContexts, "current-context: second")))
	if err != nil {
		t.Fatalf("parseKubeconfig returned error: %v", err)
	}

	if refs.caPath != "/pki/second-ca.crt" {
		t.Errorf("caPath = %q, want the cluster of the current context", refs.caPath)
	}

	if refs.certPath != "/pki/second-client.pem" {
		t.Errorf("certPath = %q, want the user of the current context", refs.certPath)
	}
}

// Intent: with no usable current-context the first cluster and the first user are used, which is
// what single-cluster node kubeconfigs rely on.
func TestParseKubeconfigFallsBackToFirstClusterAndUser(t *testing.T) {
	cases := map[string]string{
		"absent current-context":  "",
		"current-context missing": "current-context: not-a-context",
		"empty current-context":   "current-context: \"\"",
	}

	for name, line := range cases {
		t.Run(name, func(t *testing.T) {
			refs, err := parseKubeconfig([]byte(fmt.Sprintf(kubeconfigTwoContexts, line)))
			if err != nil {
				t.Fatalf("parseKubeconfig returned error: %v", err)
			}

			if refs.caPath != "/pki/first-ca.crt" {
				t.Errorf("caPath = %q, want the first cluster", refs.caPath)
			}

			if refs.certPath != "/pki/first-client.pem" {
				t.Errorf("certPath = %q, want the first user", refs.certPath)
			}
		})
	}
}

// Intent: a context that names a cluster or user the file does not define yields no reference for
// that side instead of guessing another entry, since guessing would fingerprint the wrong CA.
func TestParseKubeconfigDoesNotGuessWhenContextTargetIsMissing(t *testing.T) {
	data := `current-context: broken
clusters:
- name: first-cluster
  cluster:
    certificate-authority: /pki/first-ca.crt
contexts:
- name: broken
  context:
    cluster: undefined-cluster
    user: undefined-user
users:
- name: first-user
  user:
    client-certificate: /pki/first-client.pem
`

	refs, err := parseKubeconfig([]byte(data))
	if err != nil {
		t.Fatalf("parseKubeconfig returned error: %v", err)
	}

	if refsAreEmpty(refs) == false {
		t.Errorf("parseKubeconfig guessed references for a context with undefined targets: %+v", refs)
	}
}

// Intent: private key material in a kubeconfig is never mistaken for a certificate: a user that
// carries only a client key, inline or by path, yields no certificate reference.
func TestParseKubeconfigIgnoresClientKeyWithoutCertificate(t *testing.T) {
	client := newTestCert(t, "system:node:node-1", 1)
	keyB64 := base64.StdEncoding.EncodeToString(client.keyPEM)

	users := map[string]string{
		"client-key-data": "    client-key-data: " + keyB64 + "\n",
		"client-key":      "    client-key: /var/lib/kubelet/pki/kubelet-client-current.pem\n",
	}

	for name, userBody := range users {
		t.Run(name, func(t *testing.T) {
			data := "current-context: default\n" +
				"clusters:\n- name: kubernetes\n  cluster:\n    server: https://10.0.0.1:6443\n" +
				"contexts:\n- name: default\n  context:\n    cluster: kubernetes\n    user: kubelet\n" +
				"users:\n- name: kubelet\n  user:\n" + userBody

			refs, err := parseKubeconfig([]byte(data))
			if err != nil {
				t.Fatalf("parseKubeconfig returned error: %v", err)
			}

			if refsAreEmpty(refs) == false {
				t.Errorf("parseKubeconfig returned references %+v for a user with only %s", refs, name)
			}
		})
	}
}

// Intent: arbitrary file content never crashes the agent: every bad input yields an error or no references at all.
func TestParseKubeconfigNoPanic(t *testing.T) {
	validKubeconfig := fmt.Sprintf(kubeconfigEmbeddedTemplate, "QUJD", "QUJD", "QUJD")

	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"clusters:",               // key with no value
		"clusters: 5",             // wrong type for a list
		"clusters:\n- cluster: 5", // wrong type for the nested object
		"clusters:\n- name: a\n  cluster:\n    certificate-authority-data: [1, 2]", // wrong type for a string
		"users:\n- user:\n    client-certificate-data: !!!",                        // invalid base64
		"contexts:\n- name: a\n  context: null\ncurrent-context: a",                // null context body
		"current-context: a\ncontexts: [{name: a}]",                                // context without cluster or user
		"- just\n- a\n- list",                    // document is a sequence
		"just a scalar",                          // document is a scalar
		"clusters:\n- name: a\n  name: b",        // duplicate key
		truncatedInput(validKubeconfig),          // only the first line
		validKubeconfig[:len(validKubeconfig)/2], // cut off mid-document
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseKubeconfig panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()

			refs, err := parseKubeconfig([]byte(input))
			if (err == nil) && (refsAreEmpty(refs) == false) {
				t.Errorf("parseKubeconfig returned references %+v without an error for input %q", refs, input[:min(len(input), 50)])
			}
		})
	}
}

// --- distro version parsers ---

type versionParserCase struct {
	name   string
	parse  func(string) string
	sample string
	want   string
	// split is a valid sample whose first line is cut short, leaving the version on a later line.
	split string
	// truncated are inputs cut off partway through the first line of a valid sample.
	truncated []string
}

func versionParserCases() []versionParserCase {
	return []versionParserCase{
		{
			name:      "parseK3sVersion",
			parse:     parseK3sVersion,
			sample:    "k3s version v1.30.1+k3s1 (a1b2c3d4)\ngo version go1.22.2\n",
			want:      "v1.30.1+k3s1",
			split:     "k3s version\nv1.30.1+k3s1 (a1b2c3d4)\n",
			truncated: []string{"k3s", "k3s version", "k3s version v", "k3s version vx1.30"},
		},
		{
			name:      "parseRke2Version",
			parse:     parseRke2Version,
			sample:    "rke2 version v1.30.1+rke2r1 (a1b2c3d4)\ngo version go1.22.2 X:boringcrypto\n",
			want:      "v1.30.1+rke2r1",
			split:     "rke2 version\nv1.30.1+rke2r1 (a1b2c3d4)\n",
			truncated: []string{"rke2", "rke2 version", "rke2 version v", "rke2 version vx1.30"},
		},
		{
			name:      "parseKubeletVersion",
			parse:     parseKubeletVersion,
			sample:    "Kubernetes v1.30.1\n",
			want:      "v1.30.1",
			split:     "Kubernetes\nv1.30.1\n",
			truncated: []string{"Kubernetes", "Kubernetes v", "Kubernetes vx1.30"},
		},
		{
			name:      "parseK0sVersion",
			parse:     parseK0sVersion,
			sample:    "v1.30.1+k0s.0\n",
			want:      "v1.30.1+k0s.0",
			split:     "\nv1.30.1+k0s.0\n",
			truncated: []string{"v", "vx1.30"},
		},
	}
}

// Intent: each distro's version output yields the bare version string the server shows, including
// the distro suffix ("+k3s1", "-eks-abc") that tells releases apart.
func TestKubeDistroVersionParsersReadSampleOutput(t *testing.T) {
	for _, tc := range versionParserCases() {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.parse(tc.sample); got != tc.want {
				t.Errorf("%s(%q) = %q, want %q", tc.name, tc.sample, got, tc.want)
			}
		})
	}

	if got := parseKubeletVersion("Kubernetes v1.30.1-eks-abc"); got != "v1.30.1-eks-abc" {
		t.Errorf("parseKubeletVersion = %q, want %q", got, "v1.30.1-eks-abc")
	}
}

// Intent: command output captured from Windows-style or padded sources parses identically to the
// clean output, so line endings and stray whitespace never change the reported version.
func TestKubeDistroVersionParsersTolerateCrlfAndWhitespace(t *testing.T) {
	for _, tc := range versionParserCases() {
		t.Run(tc.name, func(t *testing.T) {
			variants := map[string]string{
				"crlf":       strings.ReplaceAll(tc.sample, "\n", "\r\n"),
				"padded":     "  " + strings.Replace(tc.sample, "\n", "  \t\n", 1),
				"no newline": truncatedInput(tc.sample),
			}

			for variant, input := range variants {
				if got := tc.parse(input); got != tc.want {
					t.Errorf("%s(%s %q) = %q, want %q", tc.name, variant, input, got, tc.want)
				}
			}
		})
	}
}

// Intent: only the first line is read: a later line that looks like a version never rescues a
// first line that is not one.
func TestKubeDistroVersionParsersReadOnlyFirstLine(t *testing.T) {
	for _, tc := range versionParserCases() {
		t.Run(tc.name, func(t *testing.T) {
			for _, input := range []string{"some unrelated banner\n" + tc.sample, tc.split} {
				if got := tc.parse(input); got != "" {
					t.Errorf("%s(%q) = %q, want empty", tc.name, input, got)
				}
			}
		})
	}
}

// Intent: a string that is not a version (no leading v and digit) is rejected rather than echoed,
// so error text printed by a failing binary is never reported as a version.
func TestKubeDistroVersionParsersRejectNonVersionValues(t *testing.T) {
	inputs := map[string][]string{
		"parseK3sVersion":     {"k3s version 1.30.1+k3s1 (a1b2c3d4)", "k3s version vnext (abc)", "k3s: command not found"},
		"parseRke2Version":    {"rke2 version 1.30.1+rke2r1 (a1b2c3d4)", "rke2 version vnext (abc)", "rke2: command not found"},
		"parseKubeletVersion": {"Kubernetes 1.30.1", "Kubernetes vnext", "kubelet: command not found"},
		"parseK0sVersion":     {"1.30.1+k0s.0", "vnext", "k0s: command not found", "v1.30.1 extra words"},
	}

	for _, tc := range versionParserCases() {
		for _, input := range inputs[tc.name] {
			if got := tc.parse(input); got != "" {
				t.Errorf("%s(%q) = %q, want empty", tc.name, input, got)
			}
		}
	}

	if got := parseK3sVersion("rke2 version v1.30.1+rke2r1 (a1b2c3d4)"); got != "" {
		t.Errorf("parseK3sVersion accepted rke2 output and returned %q", got)
	}

	if got := parseRke2Version("k3s version v1.30.1+k3s1 (a1b2c3d4)"); got != "" {
		t.Errorf("parseRke2Version accepted k3s output and returned %q", got)
	}
}

// Intent: garbage or half-written command output never crashes the agent and never yields a version.
func TestKubeDistroVersionParsersNoPanic(t *testing.T) {
	for _, tc := range versionParserCases() {
		inputs := append([]string{}, commonBadInputs...)
		inputs = append(inputs, tc.truncated...)

		for _, input := range inputs {
			t.Run("", func(t *testing.T) {
				defer func() {
					if r := recover(); r != nil {
						t.Errorf("%s panicked on input %q: %v", tc.name, input[:min(len(input), 50)], r)
					}
				}()

				if got := tc.parse(input); got != "" {
					t.Errorf("%s(%q) = %q, want empty", tc.name, input[:min(len(input), 50)], got)
				}
			})
		}
	}
}

// --- parseSnapVersion ---

// Intent: snap.yaml is hand-edited and generated by different tools, so the version is read
// whichever way it is quoted.
func TestParseSnapVersionHandlesQuotingStyles(t *testing.T) {
	cases := map[string]string{
		"bare":          "name: microk8s\nversion: v1.30.1\nsummary: Kubernetes\n",
		"single quoted": "name: microk8s\nversion: 'v1.30.1'\nsummary: Kubernetes\n",
		"double quoted": "name: microk8s\nversion: \"v1.30.1\"\nsummary: Kubernetes\n",
		"crlf":          "name: microk8s\r\nversion: v1.30.1\r\nsummary: Kubernetes\r\n",
		"padded":        "name: microk8s\nversion:   v1.30.1   \nsummary: Kubernetes\n",
		"only line":     "version: v1.30.1",
	}

	for name, input := range cases {
		t.Run(name, func(t *testing.T) {
			if got := parseSnapVersion(input); got != "v1.30.1" {
				t.Errorf("parseSnapVersion(%q) = %q, want %q", input, got, "v1.30.1")
			}
		})
	}
}

// Intent: a version: key nested under another key (an app or part) is not the snap's version, so
// only the top-level line counts, wherever it appears.
func TestParseSnapVersionIgnoresIndentedVersionKey(t *testing.T) {
	nestedOnly := "name: microk8s\napps:\n  kubelet:\n    version: v9.9.9\n"
	if got := parseSnapVersion(nestedOnly); got != "" {
		t.Errorf("parseSnapVersion read a nested version and returned %q", got)
	}

	nestedFirst := "apps:\n  kubelet:\n    version: v9.9.9\nversion: v1.30.1\n"
	if got := parseSnapVersion(nestedFirst); got != "v1.30.1" {
		t.Errorf("parseSnapVersion = %q, want the top-level version %q", got, "v1.30.1")
	}
}

// Intent: the first top-level version line wins and is not skipped when it is invalid, so a later
// well-formed line cannot override what the file declares first.
func TestParseSnapVersionUsesFirstTopLevelLine(t *testing.T) {
	if got := parseSnapVersion("version: v1.30.1\nversion: v2.0.0\n"); got != "v1.30.1" {
		t.Errorf("parseSnapVersion = %q, want the first version %q", got, "v1.30.1")
	}

	if got := parseSnapVersion("version: latest\nversion: v2.0.0\n"); got != "" {
		t.Errorf("parseSnapVersion = %q, want empty when the first version line is not a version", got)
	}
}

// Intent: a key that merely starts with the word version, or a value that is not a v-prefixed
// version, is not reported.
func TestParseSnapVersionRejectsNonVersionValues(t *testing.T) {
	cases := []string{
		"versions: v1.30.1",
		"version-info: v1.30.1",
		"version: 1.30.1",
		"version: 'latest'",
		"version:",
		"version: \"\"",
		"version: v",
		"version: vnext",
		"version: 'v1.30.1\"",
	}

	for _, input := range cases {
		if got := parseSnapVersion(input); got != "" {
			t.Errorf("parseSnapVersion(%q) = %q, want empty", input, got)
		}
	}
}

// Intent: garbage or truncated snap.yaml content never crashes the agent and never yields a version.
func TestParseSnapVersionNoPanic(t *testing.T) {
	inputs := append([]string{}, commonBadInputs...)
	inputs = append(inputs,
		"version",      // key without colon
		"version:\x00", // NUL after the key
		"version: '",   // lone quote
		"version: \"",  // lone quote
		"version: ''",  // empty quoted value
		truncatedInput("name: microk8s\nversion: v1.30.1\n"), // only the line before the version
	)

	for _, input := range inputs {
		t.Run("", func(t *testing.T) {
			defer func() {
				if r := recover(); r != nil {
					t.Errorf("parseSnapVersion panicked on input %q: %v", input[:min(len(input), 50)], r)
				}
			}()

			if got := parseSnapVersion(input); got != "" {
				t.Errorf("parseSnapVersion(%q) = %q, want empty", input[:min(len(input), 50)], got)
			}
		})
	}
}
