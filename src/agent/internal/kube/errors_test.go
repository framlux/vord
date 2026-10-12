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
	"net/http"
	"net/url"
	"strings"
	"testing"
)

// Intent: a 409 is told apart by its reason, because creating an object that exists and updating from a stale
// resourceVersion need opposite handling.
func TestAPIErrorPredicatesDistinguishConflictFromAlreadyExists(t *testing.T) {
	alreadyExists := &APIError{Code: http.StatusConflict, Reason: "AlreadyExists", Message: "leases \"x\" already exists"}
	conflict := &APIError{Code: http.StatusConflict, Reason: "Conflict", Message: "the object has been modified"}
	bare409 := &APIError{Code: http.StatusConflict}

	cases := []struct {
		name          string
		err           error
		alreadyExists bool
		conflict      bool
	}{
		{"AlreadyExists", alreadyExists, true, false},
		{"Conflict", conflict, false, true},
		{"409 without a reason is neither", bare409, false, false},
		{"wrapped AlreadyExists", fmt.Errorf("create lease: %w", alreadyExists), true, false},
		{"wrapped Conflict", fmt.Errorf("update lease: %w", conflict), false, true},
		{"nil", nil, false, false},
		{"unrelated error", errors.New("boom"), false, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := IsAlreadyExists(tc.err); got != tc.alreadyExists {
				t.Errorf("IsAlreadyExists = %v, want %v", got, tc.alreadyExists)
			}

			if got := IsConflict(tc.err); got != tc.conflict {
				t.Errorf("IsConflict = %v, want %v", got, tc.conflict)
			}
		})
	}
}

func TestAPIErrorPredicatesNotFoundAndAuthRejected(t *testing.T) {
	cases := []struct {
		name         string
		err          error
		notFound     bool
		authRejected bool
	}{
		{"404", &APIError{Code: http.StatusNotFound, Reason: "NotFound"}, true, false},
		{"401", &APIError{Code: http.StatusUnauthorized}, false, true},
		{"403", &APIError{Code: http.StatusForbidden, Reason: "Forbidden"}, false, true},
		{"500", &APIError{Code: http.StatusInternalServerError}, false, false},
		{"wrapped 403", fmt.Errorf("get lease: %w", &APIError{Code: http.StatusForbidden}), false, true},
		{"nil", nil, false, false},
		{"unrelated error", errors.New("boom"), false, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := IsNotFound(tc.err); got != tc.notFound {
				t.Errorf("IsNotFound = %v, want %v", got, tc.notFound)
			}

			if got := IsAuthRejected(tc.err); got != tc.authRejected {
				t.Errorf("IsAuthRejected = %v, want %v", got, tc.authRejected)
			}
		})
	}
}

// Intent: IsTLSVerify finds the three verification failures, however deeply a transport wraps them, and nothing
// else, so a refused client certificate or a dead network is not reported as a rotated CA.
func TestIsTLSVerify(t *testing.T) {
	unknownAuthority := x509.UnknownAuthorityError{}
	invalid := x509.CertificateInvalidError{Reason: x509.Expired}
	hostname := x509.HostnameError{Host: "example.invalid"}

	cases := []struct {
		name string
		err  error
		want bool
	}{
		{"unknown authority", unknownAuthority, true},
		{"invalid certificate", invalid, true},
		{"hostname mismatch", hostname, true},
		{"wrapped by the TLS layer", &tls.CertificateVerificationError{Err: unknownAuthority}, true},
		{"TLS layer verification failure of another kind", &tls.CertificateVerificationError{Err: errors.New("no usable roots")}, true},
		{"wrapped by the URL error", &url.Error{Op: "Get", URL: "https://x", Err: &tls.CertificateVerificationError{Err: hostname}}, true},
		{"wrapped with %w", fmt.Errorf("get lease: %w", &url.Error{Op: "Get", URL: "https://x", Err: invalid}), true},
		{"nil", nil, false},
		{"unrelated error", errors.New("boom"), false},
		{"deadline", context.DeadlineExceeded, false},
		{"API error", &APIError{Code: http.StatusUnauthorized}, false},
		{"remote bad certificate alert", &url.Error{Op: "Get", URL: "https://x", Err: errors.New("remote error: tls: bad certificate")}, false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := IsTLSVerify(tc.err); got != tc.want {
				t.Errorf("IsTLSVerify = %v, want %v", got, tc.want)
			}
		})
	}
}

// Intent: a Status body fills reason and message, and the HTTP status code stays authoritative even when the body
// disagrees.
func TestNewAPIErrorReadsStatusObject(t *testing.T) {
	body := `{"kind":"Status","apiVersion":"v1","status":"Failure","message":"leases \"vord-reporter\" already exists","reason":"AlreadyExists","code":418}`

	apiError := newAPIError(http.StatusConflict, []byte(body))

	if apiError.Code != http.StatusConflict {
		t.Errorf("Code = %d, want the HTTP status %d", apiError.Code, http.StatusConflict)
	}

	if apiError.Reason != "AlreadyExists" {
		t.Errorf("Reason = %q, want AlreadyExists", apiError.Reason)
	}

	if apiError.Message != `leases "vord-reporter" already exists` {
		t.Errorf("Message = %q", apiError.Message)
	}
}

// Intent: an error body that is not a Status, including the non-JSON body of a 401, still yields an error that
// carries the HTTP code.
func TestNewAPIErrorWithoutStatusObject(t *testing.T) {
	cases := []struct {
		name        string
		body        string
		wantMessage string
	}{
		{"plain text", "Unauthorized", "Unauthorized"},
		{"html", "<html><body>401 Authorization Required</body></html>", "<html><body>401 Authorization Required</body></html>"},
		{"empty", "", ""},
		{"json that is not a status", `{"error":"nope"}`, `{"error":"nope"}`},
		{"json array", `[1,2]`, `[1,2]`},
		{"binary", "\x00\x01\xff\xfe", "??"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			apiError := newAPIError(http.StatusUnauthorized, []byte(tc.body))

			if apiError.Code != http.StatusUnauthorized {
				t.Errorf("Code = %d, want 401", apiError.Code)
			}

			if apiError.Reason != "" {
				t.Errorf("Reason = %q, want empty", apiError.Reason)
			}

			if apiError.Message != tc.wantMessage {
				t.Errorf("Message = %q, want %q", apiError.Message, tc.wantMessage)
			}

			if IsAuthRejected(apiError) == false {
				t.Errorf("a 401 must satisfy IsAuthRejected")
			}
		})
	}
}

// Intent: server-supplied text is bounded and cannot carry control characters into a log line.
func TestNewAPIErrorSanitisesServerText(t *testing.T) {
	long := strings.Repeat("a", 10_000)

	apiError := newAPIError(http.StatusInternalServerError, []byte(long))
	if len(apiError.Message) > maxErrorTextBytes+len("...") {
		t.Errorf("Message is %d bytes, want at most %d", len(apiError.Message), maxErrorTextBytes+len("..."))
	}

	apiError = newAPIError(http.StatusBadGateway, []byte("line one\r\nline two\x1b[31m\x00"))
	for _, r := range apiError.Message {
		if r < ' ' || r == 0x7f {
			t.Errorf("Message %q holds control character %q", apiError.Message, r)
		}
	}

	status := `{"reason":"Bad\nReason","message":"multi\nline\u001b"}`

	apiError = newAPIError(http.StatusForbidden, []byte(status))
	if strings.ContainsAny(apiError.Reason+apiError.Message, "\n\x1b") {
		t.Errorf("Status text kept control characters: %q %q", apiError.Reason, apiError.Message)
	}
}

func TestAPIErrorErrorText(t *testing.T) {
	cases := []struct {
		err  *APIError
		want string
	}{
		{&APIError{Code: 409, Reason: "AlreadyExists", Message: "already there"}, "kubernetes API returned 409 AlreadyExists: already there"},
		{&APIError{Code: 401, Message: "denied"}, "kubernetes API returned 401 Unauthorized: denied"},
		{&APIError{Code: 404}, "kubernetes API returned 404 Not Found"},
		{&APIError{Code: 599}, "kubernetes API returned 599"},
	}

	for _, tc := range cases {
		if got := tc.err.Error(); got != tc.want {
			t.Errorf("Error() = %q, want %q", got, tc.want)
		}
	}
}
