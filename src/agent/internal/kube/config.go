// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"encoding/base64"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// maxKubeconfigFileBytes caps every file the default reader loads. A kubeconfig and the files it points at are a
// few KB, so anything larger is not one.
const maxKubeconfigFileBytes = 1 << 20

// Config is what the client needs to reach one cluster and prove who it is. Credentials are held in memory only.
// Formatting a Config with any fmt verb, or logging it with slog, prints a redacted summary, so it is safe to log.
type Config struct {
	Server        string
	CAData        []byte
	TLSServerName string
	ClientCert    []byte // PEM
	ClientKey     []byte // PEM
	Token         string
}

// String describes the Config without any credential: the server (without URL credentials), the TLS server name
// and which secrets are present.
func (c Config) String() string {
	return fmt.Sprintf("kube.Config{Server: %s, TLSServerName: %q, CA: %s, ClientCert: %s, ClientKey: %s, Token: %s}",
		redactedServer(c.Server), c.TLSServerName, presence(len(c.CAData) > 0), presence(len(c.ClientCert) > 0),
		presence(len(c.ClientKey) > 0), presence(c.Token != ""))
}

// GoString is the %#v form, identical to String so the Go-syntax dump cannot leak either.
func (c Config) GoString() string {
	return c.String()
}

// LogValue is the form structured logging uses, so a Config passed to slog is redacted by the JSON handler too,
// which would otherwise encode every exported field.
func (c Config) LogValue() slog.Value {
	return slog.StringValue(c.String())
}

// presence names whether a value is set without revealing it.
func presence(present bool) string {
	if present {
		return "[redacted]"
	}

	return "[none]"
}

// redactedServer returns server with any user information removed, or a placeholder when it does not parse.
func redactedServer(server string) string {
	parsed, err := url.Parse(server)
	if err != nil {
		return "[invalid]"
	}

	return parsed.Redacted()
}

// kubeconfigCluster declares the cluster fields the client uses. proxy-url is deliberately absent: the client
// never uses a proxy.
type kubeconfigCluster struct {
	Server                   string `yaml:"server"`
	TLSServerName            string `yaml:"tls-server-name"`
	CertificateAuthority     string `yaml:"certificate-authority"`
	CertificateAuthorityData string `yaml:"certificate-authority-data"`
	InsecureSkipTLSVerify    any    `yaml:"insecure-skip-tls-verify"`
}

// kubeconfigUser declares the user fields the client supports, plus the ones that must be seen in order to be
// refused.
type kubeconfigUser struct {
	ClientCertificate     string `yaml:"client-certificate"`
	ClientCertificateData string `yaml:"client-certificate-data"`
	ClientKey             string `yaml:"client-key"`
	ClientKeyData         string `yaml:"client-key-data"`
	Token                 string `yaml:"token"`
	TokenFile             string `yaml:"tokenFile"`
	Exec                  any    `yaml:"exec"`
	AuthProvider          any    `yaml:"auth-provider"`
	Username              string `yaml:"username"`
	Password              string `yaml:"password"`
}

type kubeconfigDocument struct {
	CurrentContext string `yaml:"current-context"`
	Clusters       []struct {
		Name    string            `yaml:"name"`
		Cluster kubeconfigCluster `yaml:"cluster"`
	} `yaml:"clusters"`
	Contexts []struct {
		Name    string `yaml:"name"`
		Context struct {
			Cluster string `yaml:"cluster"`
			User    string `yaml:"user"`
		} `yaml:"context"`
	} `yaml:"contexts"`
	Users []struct {
		Name string         `yaml:"name"`
		User kubeconfigUser `yaml:"user"`
	} `yaml:"users"`
}

// LoadKubeconfig reads the kubeconfig at path and returns the cluster and user of its current-context. When
// current-context is absent, or names a context the file does not hold, the first context is used. A context
// whose cluster or user the file does not define is an error, because using another entry would send credentials
// to the wrong cluster.
//
// Inline *-data values win over the matching path, and relative paths resolve against the kubeconfig's directory.
// A tokenFile is read once and its surrounding whitespace removed; an inline token wins over it. readFile reads
// the kubeconfig and every file it names, and nil means a size-capped read of the real filesystem.
//
// The kubeconfig is refused, before any credential file is opened, when:
//   - its cluster sets insecure-skip-tls-verify in any way other than false: ErrInsecureKubeconfig;
//   - its user needs an exec plugin, an auth provider or a username and password: ErrUnsupportedAuth;
//   - its cluster names no certificate authority: ErrNoCA.
//
// Errors never quote file content, because a kubeconfig scalar can be a credential.
func LoadKubeconfig(path string, readFile func(string) ([]byte, error)) (Config, error) {
	if readFile == nil {
		readFile = readBoundedFile
	}

	raw, err := readFile(path)
	if err != nil {
		return Config{}, fmt.Errorf("read kubeconfig: %w", err)
	}

	var doc kubeconfigDocument
	if err := yaml.Unmarshal(raw, &doc); err != nil {
		return Config{}, errors.New("parse kubeconfig: malformed YAML")
	}

	cluster, user, err := selectContext(&doc)
	if err != nil {
		return Config{}, err
	}

	if insecureRequested(cluster.InsecureSkipTLSVerify) {
		return Config{}, ErrInsecureKubeconfig
	}

	if (user != nil) && usesUnsupportedAuth(user) {
		return Config{}, ErrUnsupportedAuth
	}

	if strings.TrimSpace(cluster.Server) == "" {
		return Config{}, errors.New("kubeconfig cluster has no server")
	}

	loader := fileLoader{dir: filepath.Dir(path), readFile: readFile}

	cfg := Config{Server: cluster.Server, TLSServerName: cluster.TLSServerName}

	cfg.CAData, err = loader.load("certificate-authority", cluster.CertificateAuthorityData, cluster.CertificateAuthority)
	if err != nil {
		return Config{}, err
	}

	if len(cfg.CAData) == 0 {
		return Config{}, ErrNoCA
	}

	if user == nil {
		return cfg, nil
	}

	cfg.ClientCert, err = loader.load("client-certificate", user.ClientCertificateData, user.ClientCertificate)
	if err != nil {
		return Config{}, err
	}

	cfg.ClientKey, err = loader.load("client-key", user.ClientKeyData, user.ClientKey)
	if err != nil {
		return Config{}, err
	}

	cfg.Token, err = loader.token(user.Token, user.TokenFile)
	if err != nil {
		return Config{}, err
	}

	return cfg, nil
}

// selectContext follows current-context to its cluster and user, falling back to the first context. The returned
// user is nil when the context names none.
func selectContext(doc *kubeconfigDocument) (*kubeconfigCluster, *kubeconfigUser, error) {
	contextIndex := -1

	if doc.CurrentContext != "" {
		for i := range doc.Contexts {
			if doc.Contexts[i].Name == doc.CurrentContext {
				contextIndex = i

				break
			}
		}
	}

	if (contextIndex < 0) && (len(doc.Contexts) > 0) {
		contextIndex = 0
	}

	if contextIndex < 0 {
		return nil, nil, errors.New("kubeconfig has no context")
	}

	chosen := doc.Contexts[contextIndex].Context

	var cluster *kubeconfigCluster

	for i := range doc.Clusters {
		if (chosen.Cluster != "") && (doc.Clusters[i].Name == chosen.Cluster) {
			cluster = &doc.Clusters[i].Cluster

			break
		}
	}

	if cluster == nil {
		return nil, nil, errors.New("kubeconfig context names a cluster the file does not define")
	}

	if chosen.User == "" {
		return cluster, nil, nil
	}

	for i := range doc.Users {
		if doc.Users[i].Name == chosen.User {
			return cluster, &doc.Users[i].User, nil
		}
	}

	return nil, nil, errors.New("kubeconfig context names a user the file does not define")
}

// insecureRequested reports whether insecure-skip-tls-verify asks to skip verification. It fails closed: only an
// absent, null or false value (as a boolean or the string "false") counts as not asking.
func insecureRequested(value any) bool {
	switch typed := value.(type) {
	case nil:
		return false
	case bool:
		return typed
	case string:
		return strings.EqualFold(strings.TrimSpace(typed), "false") == false
	default:
		return true
	}
}

// usesUnsupportedAuth reports whether the user relies on an exec plugin, an auth provider or basic
// authentication. A null exec or auth-provider key is not a request for one.
func usesUnsupportedAuth(user *kubeconfigUser) bool {
	return (user.Exec != nil) || (user.AuthProvider != nil) || (user.Username != "") || (user.Password != "")
}

// fileLoader resolves kubeconfig references to bytes.
type fileLoader struct {
	dir      string
	readFile func(string) ([]byte, error)
}

// resolve returns path made absolute against the kubeconfig's directory.
func (l fileLoader) resolve(path string) string {
	if filepath.IsAbs(path) {
		return path
	}

	return filepath.Join(l.dir, path)
}

// load returns the decoded inline value when there is one, otherwise the contents of the referenced file, and nil
// when the kubeconfig sets neither.
func (l fileLoader) load(label string, inline string, path string) ([]byte, error) {
	if compact := strings.Join(strings.Fields(inline), ""); compact != "" {
		decoded, err := base64.StdEncoding.DecodeString(compact)
		if err != nil {
			return nil, fmt.Errorf("%s-data: %w", label, err)
		}

		return decoded, nil
	}

	if path == "" {
		return nil, nil
	}

	data, err := l.readFile(l.resolve(path))
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", label, err)
	}

	return data, nil
}

// token returns the inline token, or the contents of the token file when there is no inline token. Surrounding
// whitespace is removed from either, because a line break in a bearer header is invalid.
func (l fileLoader) token(inline string, path string) (string, error) {
	if trimmed := strings.TrimSpace(inline); trimmed != "" {
		return trimmed, nil
	}

	if path == "" {
		return "", nil
	}

	data, err := l.readFile(l.resolve(path))
	if err != nil {
		return "", fmt.Errorf("read tokenFile: %w", err)
	}

	token := strings.TrimSpace(string(data))
	if token == "" {
		return "", errors.New("tokenFile is empty")
	}

	return token, nil
}

// readBoundedFile reads at most maxKubeconfigFileBytes from a regular file. A larger file is refused rather than
// truncated, so a half-read certificate is never used.
func readBoundedFile(path string) ([]byte, error) {
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}

	defer func() { _ = file.Close() }()

	info, err := file.Stat()
	if err != nil {
		return nil, err
	}

	if info.Mode().IsRegular() == false {
		return nil, fmt.Errorf("%s is not a regular file", path)
	}

	data, err := io.ReadAll(io.LimitReader(file, maxKubeconfigFileBytes+1))
	if err != nil {
		return nil, err
	}

	if len(data) > maxKubeconfigFileBytes {
		return nil, fmt.Errorf("%s is larger than %d bytes", path, maxKubeconfigFileBytes)
	}

	return data, nil
}
