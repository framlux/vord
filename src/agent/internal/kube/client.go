// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

// Package kube is a minimal Kubernetes REST client built on net/http and encoding/json. It carries the few calls
// the agent makes to a cluster and avoids client-go, whose dependency tree is out of proportion for the agent.
//
// Every connection is verified against the CA the caller supplies and nothing else: there is no skip-verify
// path, the system roots are not consulted, proxy settings in the environment are ignored and redirects are not
// followed, so a credential only ever travels to the configured server. The package does not log.
package kube

import (
	"bytes"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const (
	// requestTimeout bounds each request, including reading its response.
	requestTimeout = 30 * time.Second

	// maxResponseBytes caps the response body the client will buffer.
	maxResponseBytes = 32 << 20

	// maxErrorBodyBytes caps how much of an error response is read. An error body only needs to hold a Status
	// object.
	maxErrorBodyBytes = 1 << 20
)

// Client talks to one Kubernetes API server. It is safe for concurrent use.
type Client struct {
	http     *http.Client
	base     *url.URL
	token    string
	timeout  time.Duration
	maxBody  int64
	maxPages int
}

// String names the client by its server and omits the credentials, so formatting a Client cannot leak them.
func (c *Client) String() string {
	return "kube.Client{" + c.base.Redacted() + "}"
}

// GoString is the %#v form, identical to String.
func (c *Client) GoString() string {
	return c.String()
}

// New builds a client for cfg. It refuses a server that is not https, or that carries credentials in its URL, a
// missing or unusable CA (ErrNoCA when there is none), a client certificate without its key, and a token that
// would corrupt a header. Errors never quote the credentials.
func New(cfg Config) (*Client, error) {
	base, err := parseServer(cfg.Server)
	if err != nil {
		return nil, err
	}

	if len(cfg.CAData) == 0 {
		return nil, ErrNoCA
	}

	roots := x509.NewCertPool()
	if roots.AppendCertsFromPEM(cfg.CAData) == false {
		return nil, errors.New("the certificate authority data holds no valid certificate")
	}

	tlsConfig := &tls.Config{
		MinVersion: tls.VersionTLS12,
		RootCAs:    roots,
		ServerName: cfg.TLSServerName,
	}

	if (len(cfg.ClientCert) > 0) != (len(cfg.ClientKey) > 0) {
		return nil, errors.New("a client certificate and its key must be set together")
	}

	if len(cfg.ClientCert) > 0 {
		pair, err := tls.X509KeyPair(cfg.ClientCert, cfg.ClientKey)
		if err != nil {
			return nil, fmt.Errorf("load client certificate: %w", err)
		}

		tlsConfig.Certificates = []tls.Certificate{pair}
	}

	if strings.ContainsFunc(cfg.Token, isControlRune) {
		return nil, errors.New("the token contains control characters")
	}

	// Proxy is set to nil on purpose: the zero value never consults HTTP_PROXY or HTTPS_PROXY, so the bearer
	// token cannot be routed through a host the kubeconfig did not name.
	transport := &http.Transport{
		Proxy:                 nil,
		DialContext:           (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSClientConfig:       tlsConfig,
		TLSHandshakeTimeout:   10 * time.Second,
		IdleConnTimeout:       30 * time.Second,
		MaxIdleConnsPerHost:   2,
		ExpectContinueTimeout: time.Second,
	}

	return &Client{
		http: &http.Client{
			Transport: transport,
			CheckRedirect: func(*http.Request, []*http.Request) error {
				return http.ErrUseLastResponse
			},
		},
		base:     base,
		token:    cfg.Token,
		timeout:  requestTimeout,
		maxBody:  maxResponseBytes,
		maxPages: maxListPages,
	}, nil
}

// parseServer validates the server URL without echoing it: a URL error quotes its input, and the input may carry
// credentials.
func parseServer(server string) (*url.URL, error) {
	base, err := url.Parse(server)
	if err != nil {
		return nil, errors.New("the server URL is not valid")
	}

	switch {
	case base.Scheme != "https":
		return nil, errors.New("the server URL must use https")
	case base.Host == "":
		return nil, errors.New("the server URL has no host")
	case base.User != nil:
		return nil, errors.New("the server URL must not carry credentials")
	case (base.RawQuery != "") || (base.Fragment != ""):
		return nil, errors.New("the server URL must not carry a query or fragment")
	}

	return base, nil
}

// isControlRune reports whether r is an ASCII control character.
func isControlRune(r rune) bool {
	return (r < ' ') || (r == 0x7f)
}

// Do sends a request to path (which must start with "/" and is appended to the server's own path) and decodes a
// JSON response into out. A nil body sends none, anything else is sent as JSON. A nil out, or an empty response,
// leaves out untouched. A non-success status becomes an *APIError.
func (c *Client) Do(ctx context.Context, method, path string, query url.Values, body any, out any) error {
	data, err := c.roundTrip(ctx, method, path, query, body)
	if err != nil {
		return err
	}

	if (out == nil) || (len(data) == 0) {
		return nil
	}

	if err := json.Unmarshal(data, out); err != nil {
		return fmt.Errorf("decode %s %s response: %w", method, path, err)
	}

	return nil
}

// roundTrip sends one request under its own timeout and returns the success body.
func (c *Client) roundTrip(ctx context.Context, method, path string, query url.Values, body any) ([]byte, error) {
	if strings.HasPrefix(path, "/") == false {
		return nil, errors.New("the request path must start with /")
	}

	var payload io.Reader

	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return nil, fmt.Errorf("encode %s %s request: %w", method, path, err)
		}

		payload = bytes.NewReader(encoded)
	}

	ctx, cancel := context.WithTimeout(ctx, c.timeout)
	defer cancel()

	target := *c.base
	target.Path = strings.TrimRight(c.base.Path, "/") + path
	target.RawPath = ""
	target.RawQuery = query.Encode()

	req, err := http.NewRequestWithContext(ctx, method, target.String(), payload)
	if err != nil {
		return nil, err
	}

	req.Header.Set("Accept", "application/json")

	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	if c.token != "" {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}

	resp, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}

	defer func() { _ = resp.Body.Close() }()

	success := (resp.StatusCode >= http.StatusOK) && (resp.StatusCode < http.StatusMultipleChoices)

	limit := c.maxBody
	if (success == false) && (limit > maxErrorBodyBytes) {
		limit = maxErrorBodyBytes
	}

	data, readErr := io.ReadAll(io.LimitReader(resp.Body, limit+1))

	if success == false {
		if int64(len(data)) > limit {
			data = data[:limit]
		}

		return nil, newAPIError(resp.StatusCode, data)
	}

	if readErr != nil {
		return nil, fmt.Errorf("read %s %s response: %w", method, path, readErr)
	}

	if int64(len(data)) > limit {
		return nil, fmt.Errorf("%s %s response is larger than %d bytes", method, path, limit)
	}

	return data, nil
}
