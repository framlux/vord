// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"unicode"
	"unicode/utf8"
)

// ErrUnsupportedAuth means the kubeconfig user authenticates with something other than a client certificate or
// a token. Exec plugins, auth providers and username/password pairs would need code or secrets the agent does
// not run or hold.
var ErrUnsupportedAuth = errors.New("kubeconfig user authenticates with an unsupported method (exec, auth-provider or username/password); use a client certificate or a token")

// ErrInsecureKubeconfig means the kubeconfig asks to skip server certificate verification. The agent has no
// such mode, so the kubeconfig is refused.
var ErrInsecureKubeconfig = errors.New("kubeconfig sets insecure-skip-tls-verify; the agent always verifies the cluster certificate")

// ErrNoCA means the kubeconfig names no certificate authority for the cluster, so the server certificate could
// not be verified.
var ErrNoCA = errors.New("kubeconfig cluster has no certificate authority")

// maxErrorTextBytes bounds the server-supplied text kept in an APIError.
const maxErrorTextBytes = 512

// APIError is a non-success response from the Kubernetes API server. Reason is the machine-readable reason of the
// Status object the server sent, and is empty when the body held no Status.
type APIError struct {
	Code    int
	Reason  string
	Message string
}

// Error describes the response. It carries only what the server said, never a request header or body.
func (e *APIError) Error() string {
	label := e.Reason
	if label == "" {
		label = http.StatusText(e.Code)
	}

	text := fmt.Sprintf("kubernetes API returned %d", e.Code)
	if label != "" {
		text += " " + label
	}

	if e.Message != "" {
		text += ": " + e.Message
	}

	return text
}

// statusObject is the part of a Kubernetes Status object an APIError keeps.
type statusObject struct {
	Message string `json:"message"`
	Reason  string `json:"reason"`
}

// newAPIError builds the error for a non-success response. The HTTP status code is authoritative. A body that is
// not a Status object, such as the plain text or HTML a proxy or a 401 can send, becomes the message instead.
func newAPIError(code int, body []byte) *APIError {
	apiError := &APIError{Code: code}

	var status statusObject
	if json.Unmarshal(body, &status) == nil && ((status.Reason != "") || (status.Message != "")) {
		apiError.Reason = sanitizeText(status.Reason)
		apiError.Message = sanitizeText(status.Message)

		return apiError
	}

	apiError.Message = sanitizeText(string(body))

	return apiError
}

// sanitizeText returns text trimmed, limited to maxErrorTextBytes and stripped of control characters and invalid
// UTF-8, so a hostile or broken server cannot inject line breaks or terminal escapes into a log line.
func sanitizeText(text string) string {
	var builder strings.Builder

	for i := 0; i < len(text); {
		r, size := utf8.DecodeRuneInString(text[i:])

		if builder.Len()+utf8.RuneLen(r) > maxErrorTextBytes {
			builder.WriteString("...")

			break
		}

		switch {
		case (r == utf8.RuneError) && (size == 1):
			builder.WriteByte('?')
		case (r == ' ') || unicode.IsPrint(r):
			builder.WriteRune(r)
		default:
			builder.WriteByte(' ')
		}

		i += size
	}

	return strings.TrimSpace(builder.String())
}

// apiErrorOf returns the APIError in err's chain, or nil.
func apiErrorOf(err error) *APIError {
	var apiError *APIError
	if errors.As(err, &apiError) {
		return apiError
	}

	return nil
}

// IsAlreadyExists reports whether err is a 409 whose reason is AlreadyExists, the answer to creating an object
// that is already there.
func IsAlreadyExists(err error) bool {
	apiError := apiErrorOf(err)

	return (apiError != nil) && (apiError.Code == http.StatusConflict) && (apiError.Reason == "AlreadyExists")
}

// IsConflict reports whether err is a 409 whose reason is Conflict, the answer to updating an object from a
// stale resourceVersion.
func IsConflict(err error) bool {
	apiError := apiErrorOf(err)

	return (apiError != nil) && (apiError.Code == http.StatusConflict) && (apiError.Reason == "Conflict")
}

// IsNotFound reports whether err is a 404.
func IsNotFound(err error) bool {
	apiError := apiErrorOf(err)

	return (apiError != nil) && (apiError.Code == http.StatusNotFound)
}

// IsAuthRejected reports whether err is a 401 or 403: the credentials were refused or lack the permission.
func IsAuthRejected(err error) bool {
	apiError := apiErrorOf(err)

	return (apiError != nil) && ((apiError.Code == http.StatusUnauthorized) || (apiError.Code == http.StatusForbidden))
}

// IsTLSVerify reports whether err is a failure to verify the server certificate: an unknown authority, an
// invalid certificate or a name mismatch.
func IsTLSVerify(err error) bool {
	var unknownAuthority x509.UnknownAuthorityError

	var invalid x509.CertificateInvalidError

	var hostname x509.HostnameError

	var verification *tls.CertificateVerificationError

	return errors.As(err, &unknownAuthority) ||
		errors.As(err, &invalid) ||
		errors.As(err, &hostname) ||
		errors.As(err, &verification)
}
