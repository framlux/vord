// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"os/exec"
	"strings"
	"testing"
	"time"

	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubereporter"
)

// newReporterClient builds a client that authenticates with a candidate's reporter token, as its elector does.
func newReporterClient(t *testing.T, state kubereporter.State) *kube.Client {
	t.Helper()

	client, err := kube.New(kube.Config{Server: state.Server, CAData: []byte(state.CACert), TLSServerName: state.TLSServerName, Token: state.Token})
	if err != nil {
		t.Fatalf("build the reporter client: %v", err)
	}

	return client
}

// deleteOptions is the body of a DELETE that only deletes the object with the given uid.
func deleteOptions(uid string) map[string]any {
	return map[string]any{"apiVersion": "v1", "kind": "DeleteOptions", "preconditions": map[string]any{"uid": uid}}
}

// listOneNode lists a single node, which the reporter's ClusterRole allows and enrol uses to prove a token works.
func listOneNode(client *kube.Client) error {
	return client.Do(context.Background(), http.MethodGet, "/api/v1/nodes", url.Values{"limit": {"1"}}, nil, nil)
}

// listWith lists a collection once and returns how many items came back.
func listWith(ctx context.Context, client *kube.Client, path string) (int, error) {
	total := 0

	err := client.List(ctx, path, nil, 100, func(body []byte) error {
		var page struct {
			Items []json.RawMessage `json:"items"`
		}

		if err := json.Unmarshal(body, &page); err != nil {
			return err
		}

		total += len(page.Items)

		return nil
	}, func() { total = 0 })

	return total, err
}

// expectStatus requires err to be an API error with the given status and reason.
func expectStatus(t *testing.T, what string, err error, code int, reason string) {
	t.Helper()

	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		t.Errorf("%s gave %v, want HTTP %d %s", what, err, code, reason)

		return
	}

	if (apiError.Code != code) || (apiError.Reason != reason) {
		t.Errorf("%s answers %d %q, want %d %q", what, apiError.Code, apiError.Reason, code, reason)
	}
}

// statusOf names the HTTP status and reason of an API error.
func statusOf(err error) string {
	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		return fmt.Sprintf("%v", err)
	}

	return fmt.Sprintf("HTTP %d %s", apiError.Code, apiError.Reason)
}

// eventually polls condition against the real cluster until it holds or the timeout passes.
func eventually(t *testing.T, timeout time.Duration, what string, condition func() bool) {
	t.Helper()

	deadline := time.Now().Add(timeout)

	for {
		if condition() {
			return
		}

		if time.Now().After(deadline) {
			t.Fatalf("gave up after %s waiting for %s", timeout, what)
		}

		time.Sleep(pollInterval)
	}
}

// runPodman runs a podman command and returns its trimmed output; a failure ends the test.
func runPodman(t *testing.T, podman string, args ...string) string {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	out, err := exec.CommandContext(ctx, podman, args...).CombinedOutput()
	if err != nil {
		t.Fatalf("podman %s: %v\n%s", strings.Join(args, " "), err, out)
	}

	return strings.TrimSpace(string(out))
}

func randomHex(t *testing.T, bytesLength int) string {
	t.Helper()

	buffer := make([]byte, bytesLength)
	if _, err := rand.Read(buffer); err != nil {
		t.Fatalf("random bytes: %v", err)
	}

	return hex.EncodeToString(buffer)
}

// anyNodeReady reports whether a node list holds a node whose Ready condition is True.
func anyNodeReady(nodes map[string]any) bool {
	items, _ := nodes["items"].([]any)

	for _, item := range items {
		node, _ := item.(map[string]any)
		conditions, _ := nestedValue(node, "status", "conditions").([]any)

		for _, entry := range conditions {
			condition, _ := entry.(map[string]any)

			if (condition["type"] == "Ready") && (condition["status"] == "True") {
				return true
			}
		}
	}

	return false
}

// nestedValue reads a value from nested generic JSON objects, and is nil when any step is missing.
func nestedValue(obj map[string]any, path ...string) any {
	var current any = obj

	for _, key := range path {
		next, ok := current.(map[string]any)
		if ok == false {
			return nil
		}

		current = next[key]
	}

	return current
}

// stringAt reads a string from nested generic JSON objects, and is "" when any step is missing.
func stringAt(obj map[string]any, path ...string) string {
	text, _ := nestedValue(obj, path...).(string)

	return text
}

// annotate sets one annotation on a raw object.
func annotate(obj map[string]any, key string, value string) {
	meta, _ := obj["metadata"].(map[string]any)

	annotations, ok := meta["annotations"].(map[string]any)
	if ok == false {
		annotations = map[string]any{}
		meta["annotations"] = annotations
	}

	annotations[key] = value
}

// deepCopy copies a raw object through JSON, keeping numbers exact.
func deepCopy(t *testing.T, obj map[string]any) map[string]any {
	t.Helper()

	data, err := json.Marshal(obj)
	if err != nil {
		t.Fatalf("copy an object: %v", err)
	}

	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()

	var copied map[string]any
	if err := decoder.Decode(&copied); err != nil {
		t.Fatalf("copy an object: %v", err)
	}

	return copied
}

func contains(list []string, want string) bool {
	for _, item := range list {
		if item == want {
			return true
		}
	}

	return false
}

func parseTime(t *testing.T, text string) time.Time {
	t.Helper()

	parsed, err := time.Parse(time.RFC3339Nano, text)
	if err != nil {
		t.Fatalf("parse the time %q: %v", text, err)
	}

	return parsed
}

// sameInstant reports whether the API's rendering of a MicroTime is the given instant.
func sameInstant(text string, instant time.Time) bool {
	parsed, err := time.Parse(time.RFC3339Nano, text)

	return (err == nil) && parsed.Equal(instant.Truncate(time.Microsecond))
}
