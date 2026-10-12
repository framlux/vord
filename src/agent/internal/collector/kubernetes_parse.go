// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package collector

import (
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"fmt"
	"strings"
	"unicode"

	"gopkg.in/yaml.v3"
)

// nodeCertCommonNamePrefix is the common-name prefix Kubernetes gives a kubelet client certificate.
const nodeCertCommonNamePrefix = "system:node:"

// kubeconfigRefs is what the collector needs from a kubeconfig: where the cluster CA and the client
// certificate are, either embedded or as a path. Paths are returned exactly as written in the file.
type kubeconfigRefs struct {
	caData   []byte // decoded certificate-authority-data, nil when absent
	caPath   string // certificate-authority, "" when absent
	certData []byte // decoded client-certificate-data, nil when absent
	certPath string // client-certificate, "" when absent
}

// kubeconfigDocument declares only the fields the collector reads. Credentials such as tokens,
// passwords and client keys have no field, so they are never decoded into a value the agent holds.
type kubeconfigDocument struct {
	CurrentContext string `yaml:"current-context"`
	Clusters       []struct {
		Name    string `yaml:"name"`
		Cluster struct {
			CertificateAuthority     string `yaml:"certificate-authority"`
			CertificateAuthorityData string `yaml:"certificate-authority-data"`
		} `yaml:"cluster"`
	} `yaml:"clusters"`
	Contexts []struct {
		Name    string `yaml:"name"`
		Context struct {
			Cluster string `yaml:"cluster"`
			User    string `yaml:"user"`
		} `yaml:"context"`
	} `yaml:"contexts"`
	Users []struct {
		Name string `yaml:"name"`
		User struct {
			ClientCertificate     string `yaml:"client-certificate"`
			ClientCertificateData string `yaml:"client-certificate-data"`
		} `yaml:"user"`
	} `yaml:"users"`
}

// parseKubeconfig follows current-context to its cluster and user. When current-context is absent or names a
// missing context, it falls back to the first cluster and the first user. A context that exists but names a
// cluster or user the file does not define yields no reference for that side, because picking another entry
// would fingerprint the wrong CA.
// A user authenticated by token or password yields no certificate reference. Malformed YAML or base64
// returns an error.
func parseKubeconfig(data []byte) (kubeconfigRefs, error) {
	var doc kubeconfigDocument
	if err := yaml.Unmarshal(data, &doc); err != nil {
		return kubeconfigRefs{}, fmt.Errorf("parse kubeconfig: %w", err)
	}

	clusterIndex, userIndex := -1, -1

	contextFound := false

	for _, c := range doc.Contexts {
		if (doc.CurrentContext != "") && (c.Name == doc.CurrentContext) {
			contextFound = true
			clusterIndex = indexOfName(len(doc.Clusters), c.Context.Cluster, func(i int) string { return doc.Clusters[i].Name })
			userIndex = indexOfName(len(doc.Users), c.Context.User, func(i int) string { return doc.Users[i].Name })

			break
		}
	}

	if contextFound == false {
		if len(doc.Clusters) > 0 {
			clusterIndex = 0
		}

		if len(doc.Users) > 0 {
			userIndex = 0
		}
	}

	var refs kubeconfigRefs

	if clusterIndex >= 0 {
		cluster := doc.Clusters[clusterIndex].Cluster
		refs.caPath = cluster.CertificateAuthority

		decoded, err := decodeKubeconfigData(cluster.CertificateAuthorityData)
		if err != nil {
			return kubeconfigRefs{}, fmt.Errorf("certificate-authority-data: %w", err)
		}

		refs.caData = decoded
	}

	if userIndex >= 0 {
		user := doc.Users[userIndex].User
		refs.certPath = user.ClientCertificate

		decoded, err := decodeKubeconfigData(user.ClientCertificateData)
		if err != nil {
			return kubeconfigRefs{}, fmt.Errorf("client-certificate-data: %w", err)
		}

		refs.certData = decoded
	}

	return refs, nil
}

// indexOfName returns the index of the first entry whose name equals want, or -1 when want is empty or no
// entry matches.
func indexOfName(count int, want string, nameAt func(int) string) int {
	if want == "" {
		return -1
	}

	for i := 0; i < count; i++ {
		if nameAt(i) == want {
			return i
		}
	}

	return -1
}

// decodeKubeconfigData decodes an embedded base64 value, returning nil for an absent one.
func decodeKubeconfigData(encoded string) ([]byte, error) {
	if encoded == "" {
		return nil, nil
	}

	decoded, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		return nil, err
	}

	return decoded, nil
}

// firstCertificate returns the first CERTIFICATE block of pemData, or nil when there is none. Blocks of any
// other type, including private keys, are walked past and never parsed or kept.
func firstCertificate(pemData []byte) *pem.Block {
	rest := pemData

	for {
		var block *pem.Block

		block, rest = pem.Decode(rest)
		if block == nil {
			return nil
		}

		if block.Type == "CERTIFICATE" {
			return block
		}
	}
}

// caFingerprint returns the lowercase hex SHA-256 of the DER bytes of the first CERTIFICATE block, or an error
// when the input holds no CERTIFICATE block or that block is not a valid certificate.
func caFingerprint(pemData []byte) (string, error) {
	block := firstCertificate(pemData)
	if block == nil {
		return "", errors.New("no certificate block found")
	}

	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return "", fmt.Errorf("parse certificate: %w", err)
	}

	sum := sha256.Sum256(cert.Raw)

	return hex.EncodeToString(sum[:]), nil
}

// nodeNameFromCert returns <name> from the CN "system:node:<name>" of the first CERTIFICATE block. It skips
// every other block type, including private keys, without decoding them. It returns an error when no block
// holds a valid certificate with that CN form.
func nodeNameFromCert(pemData []byte) (string, error) {
	block := firstCertificate(pemData)
	if block == nil {
		return "", errors.New("no certificate block found")
	}

	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return "", fmt.Errorf("parse certificate: %w", err)
	}

	name, found := strings.CutPrefix(cert.Subject.CommonName, nodeCertCommonNamePrefix)
	if (found == false) || (name == "") {
		return "", errors.New("certificate common name is not a node identity")
	}

	return name, nil
}

// firstLine returns the first line of output without its line ending or surrounding whitespace.
func firstLine(output string) string {
	line, _, _ := strings.Cut(output, "\n")

	return strings.TrimSpace(line)
}

// versionToken returns candidate when it is a single token that starts with "v" followed by a digit, and ""
// otherwise.
func versionToken(candidate string) string {
	if (len(candidate) < 2) || (candidate[0] != 'v') || (candidate[1] < '0') || (candidate[1] > '9') {
		return ""
	}

	if strings.IndexFunc(candidate, unicode.IsSpace) >= 0 {
		return ""
	}

	return candidate
}

// parseDistroBanner reads "<program> version <version> ..." from the first line of output.
func parseDistroBanner(output string, program string) string {
	fields := strings.Fields(firstLine(output))
	if (len(fields) < 3) || (fields[0] != program) || (fields[1] != "version") {
		return ""
	}

	return versionToken(fields[2])
}

// parseK3sVersion reads "k3s version v1.30.1+k3s1 (a1b2c3d4)" and returns "v1.30.1+k3s1".
func parseK3sVersion(output string) string {
	return parseDistroBanner(output, "k3s")
}

// parseRke2Version reads "rke2 version v1.30.1+rke2r1 (a1b2c3d4)" and returns "v1.30.1+rke2r1".
func parseRke2Version(output string) string {
	return parseDistroBanner(output, "rke2")
}

// parseKubeletVersion reads "Kubernetes v1.30.1" and returns "v1.30.1".
func parseKubeletVersion(output string) string {
	fields := strings.Fields(firstLine(output))
	if (len(fields) < 2) || (fields[0] != "Kubernetes") {
		return ""
	}

	return versionToken(fields[1])
}

// parseK0sVersion reads a line holding only the version, such as "v1.30.1+k0s.0".
func parseK0sVersion(output string) string {
	fields := strings.Fields(firstLine(output))
	if len(fields) != 1 {
		return ""
	}

	return versionToken(fields[0])
}

// parseSnapVersion returns the value of the first top-level "version:" line of a snap.yaml, whether it is
// bare, single quoted or double quoted. An indented version: under another key is not the snap's version.
func parseSnapVersion(snapYaml string) string {
	for _, line := range strings.Split(snapYaml, "\n") {
		value, found := strings.CutPrefix(strings.TrimRight(line, " \t\r"), "version:")
		if found == false {
			continue
		}

		value = strings.TrimSpace(value)
		if (len(value) >= 2) && ((value[0] == '\'') || (value[0] == '"')) && (value[len(value)-1] == value[0]) {
			value = value[1 : len(value)-1]
		}

		return versionToken(value)
	}

	return ""
}
