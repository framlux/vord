// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"reflect"
	"strconv"
	"sync"
	"testing"
)

const leasePath = "/apis/coordination.k8s.io/v1/namespaces/vord-system/leases/vord-reporter"

// Intent: read-modify-write through GetRaw and PutRaw keeps every field the agent does not know. The server
// stands in for an API version newer than the agent, and what it stored must come back unchanged except for the
// field the agent edited.
func TestRawReadModifyWritePreservesUnknownFields(t *testing.T) {
	stored := map[string]any{
		"apiVersion": "coordination.k8s.io/v1",
		"kind":       "Lease",
		"metadata": map[string]any{
			"name":            "vord-reporter",
			"namespace":       "vord-system",
			"resourceVersion": "41",
			"annotations":     map[string]any{"vord.framlux.io/role-version": "1", "example.com/keep": "me"},
		},
		"spec": map[string]any{
			"holderIdentity":       "vord-old",
			"leaseDurationSeconds": float64(60),
			"leaseTransitions":     float64(7),
			"strategy":             "OldestEmulationVersion",
			"preferredHolder":      "vord-other",
			"futureField":          map[string]any{"nested": []any{"a", float64(1), nil, true}},
		},
	}

	var mu sync.Mutex

	var received map[string]any

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()

		switch r.Method {
		case http.MethodGet:
			writeJSON(w, http.StatusOK, stored)
		case http.MethodPut:
			data, _ := io.ReadAll(r.Body)
			if err := json.Unmarshal(data, &received); err != nil {
				t.Errorf("PUT body: %v", err)
			}

			writeJSON(w, http.StatusOK, received)
		}
	}))

	client := newTestClient(t, configFor(server))

	lease, err := client.GetRaw(context.Background(), leasePath)
	if err != nil {
		t.Fatalf("GetRaw: %v", err)
	}

	spec, ok := lease["spec"].(map[string]any)
	if ok == false {
		t.Fatalf("spec is %T", lease["spec"])
	}

	spec["holderIdentity"] = "vord-new"
	spec["leaseTransitions"] = float64(8)

	updated, err := client.PutRaw(context.Background(), leasePath, lease)
	if err != nil {
		t.Fatalf("PutRaw: %v", err)
	}

	want := map[string]any{}

	data, _ := json.Marshal(stored)
	_ = json.Unmarshal(data, &want)

	wantSpec := want["spec"].(map[string]any)
	wantSpec["holderIdentity"] = "vord-new"
	wantSpec["leaseTransitions"] = float64(8)

	if reflect.DeepEqual(received, want) == false {
		t.Errorf("the PUT body lost or changed fields:\n got %v\nwant %v", received, want)
	}

	if received["spec"].(map[string]any)["strategy"] != "OldestEmulationVersion" {
		t.Errorf("spec.strategy was dropped: %v", received["spec"])
	}

	if reflect.DeepEqual(updated, want) == false {
		t.Errorf("PutRaw returned %v, want the stored object %v", updated, want)
	}
}

func TestPutRawUsesPutAndSurfacesConflict(t *testing.T) {
	var method string

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		method = r.Method

		writeStatus(w, http.StatusConflict, "Conflict", "the object has been modified")
	}))

	_, err := newTestClient(t, configFor(server)).PutRaw(context.Background(), leasePath, map[string]any{"kind": "Lease"})

	if method != http.MethodPut {
		t.Errorf("method = %s, want PUT", method)
	}

	if IsConflict(err) == false {
		t.Errorf("err = %v, want a Conflict", err)
	}
}

func TestGetRawProblems(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/null":
			_, _ = w.Write([]byte("null"))
		case "/array":
			_, _ = w.Write([]byte("[1]"))
		case "/empty":
			w.WriteHeader(http.StatusOK)
		default:
			writeStatus(w, http.StatusNotFound, "NotFound", "gone")
		}
	}))

	client := newTestClient(t, configFor(server))

	for _, path := range []string{"/null", "/array", "/empty"} {
		if obj, err := client.GetRaw(context.Background(), path); err == nil {
			t.Errorf("GetRaw(%s) = %v, want an error for a response that is not an object", path, obj)
		}
	}

	if _, err := client.GetRaw(context.Background(), "/absent"); IsNotFound(err) == false {
		t.Errorf("err = %v, want NotFound", err)
	}
}

// listServer answers list requests from a script of steps and records each request's query.
type listServer struct {
	mu      sync.Mutex
	steps   []listStep
	queries []url.Values
}

type listStep struct {
	status  int
	body    string
	reason  string
	wantTok string
}

func (s *listServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.queries = append(s.queries, r.URL.Query())

	if len(s.queries) > len(s.steps) {
		writeStatus(w, http.StatusInternalServerError, "InternalError", "script exhausted")

		return
	}

	step := s.steps[len(s.queries)-1]
	if got := r.URL.Query().Get("continue"); got != step.wantTok {
		writeStatus(w, http.StatusBadRequest, "BadRequest", fmt.Sprintf("request %d: continue = %q, want %q", len(s.queries), got, step.wantTok))

		return
	}

	if step.status != 0 {
		writeStatus(w, step.status, step.reason, "scripted failure")

		return
	}

	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write([]byte(step.body))
}

func pageBody(continueToken string, items ...string) string {
	quoted, _ := json.Marshal(items)

	return fmt.Sprintf(`{"kind":"PodList","metadata":{"continue":%q},"items":%s}`, continueToken, quoted)
}

// Intent: List walks every page with limit and continue, calls the callback once per page body, and leaves the
// caller's query untouched.
func TestListWalksThreePages(t *testing.T) {
	script := &listServer{steps: []listStep{
		{body: pageBody("t1", "a", "b")},
		{body: pageBody("t2", "c", "d"), wantTok: "t1"},
		{body: pageBody("", "e"), wantTok: "t2"},
	}}

	server := newTLSServer(t, script)
	client := newTestClient(t, configFor(server))

	query := url.Values{"fieldSelector": {"status.phase=Running"}}

	var pages []string

	err := client.List(context.Background(), "/api/v1/pods", query, 2, func(body []byte) error {
		pages = append(pages, string(body))

		return nil
	})
	if err != nil {
		t.Fatalf("List: %v", err)
	}

	want := []string{pageBody("t1", "a", "b"), pageBody("t2", "c", "d"), pageBody("", "e")}
	if reflect.DeepEqual(pages, want) == false {
		t.Errorf("pages = %v, want %v", pages, want)
	}

	if len(script.queries) != 3 {
		t.Fatalf("server saw %d requests, want 3", len(script.queries))
	}

	for i, got := range script.queries {
		if got.Get("limit") != "2" {
			t.Errorf("request %d: limit = %q, want 2", i, got.Get("limit"))
		}

		if got.Get("fieldSelector") != "status.phase=Running" {
			t.Errorf("request %d: fieldSelector = %q", i, got.Get("fieldSelector"))
		}
	}

	if len(query) != 1 || query.Get("limit") != "" || query.Get("continue") != "" {
		t.Errorf("the caller's query was modified: %v", query)
	}
}

func TestListWithoutLimitOmitsIt(t *testing.T) {
	script := &listServer{steps: []listStep{{body: pageBody("")}}}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	if err := client.List(context.Background(), "/api/v1/nodes", nil, 0, func([]byte) error { return nil }); err != nil {
		t.Fatalf("List: %v", err)
	}

	if _, present := script.queries[0]["limit"]; present {
		t.Errorf("limit was sent although none was asked for: %v", script.queries[0])
	}
}

// Intent: when the continue token expires (410) on page two, List starts again from page one and finishes. The
// callback sees page one twice, so a caller that accumulates must expect that.
func TestListRestartsAfterExpiredContinueToken(t *testing.T) {
	script := &listServer{steps: []listStep{
		{body: pageBody("t1", "a")},
		{status: http.StatusGone, reason: "Expired", wantTok: "t1"},
		{body: pageBody("n1", "a")},
		{body: pageBody("n2", "b"), wantTok: "n1"},
		{body: pageBody("", "c"), wantTok: "n2"},
	}}

	client := newTestClient(t, configFor(newTLSServer(t, script)))

	var pages int

	if err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil }); err != nil {
		t.Fatalf("List: %v", err)
	}

	if pages != 4 {
		t.Errorf("callback calls = %d, want 4 (page one twice, then pages two and three)", pages)
	}

	if len(script.queries) != 5 {
		t.Errorf("server saw %d requests, want 5", len(script.queries))
	}
}

// Intent: a list that keeps expiring is retried at most three times, then the 410 is returned.
func TestListGivesUpAfterThreeRestarts(t *testing.T) {
	var steps []listStep
	for i := 0; i < 5; i++ {
		steps = append(steps, listStep{body: pageBody("t" + strconv.Itoa(i)), wantTok: ""}, listStep{status: http.StatusGone, reason: "Expired", wantTok: "t" + strconv.Itoa(i)})
	}

	script := &listServer{steps: steps}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	var pages int

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil })

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusGone) {
		t.Errorf("err = %v, want the 410", err)
	}

	if pages != 4 {
		t.Errorf("callback calls = %d, want 4 (the first pass and three restarts)", pages)
	}

	if len(script.queries) != 8 {
		t.Errorf("server saw %d requests, want 8", len(script.queries))
	}
}

// Intent: only a stale continue token justifies a restart; a 410 on the very first page is just an error.
func TestListDoesNotRestartOnFirstPage(t *testing.T) {
	script := &listServer{steps: []listStep{{status: http.StatusGone, reason: "Expired"}, {body: pageBody("")}}}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil })
	if apiErrorOf(err) == nil {
		t.Errorf("err = %v, want the 410", err)
	}

	if len(script.queries) != 1 {
		t.Errorf("server saw %d requests, want 1", len(script.queries))
	}
}

func TestListStopsOnCallbackError(t *testing.T) {
	script := &listServer{steps: []listStep{{body: pageBody("t1")}, {body: pageBody(""), wantTok: "t1"}}}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	stop := errors.New("stop here")

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return stop })
	if errors.Is(err, stop) == false {
		t.Errorf("err = %v, want the callback's error", err)
	}

	if len(script.queries) != 1 {
		t.Errorf("server saw %d requests, want 1", len(script.queries))
	}
}

func TestListSurfacesOtherErrors(t *testing.T) {
	script := &listServer{steps: []listStep{{body: pageBody("t1")}, {status: http.StatusForbidden, reason: "Forbidden", wantTok: "t1"}}}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil })
	if IsAuthRejected(err) == false {
		t.Errorf("err = %v, want the 403", err)
	}
}

// Intent: a server that returns the same continue token forever cannot hold the agent in a loop.
func TestListStopsWhenContinueTokenDoesNotAdvance(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(pageBody("same")))
	}))

	var pages int

	err := newTestClient(t, configFor(server)).List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil })
	if err == nil {
		t.Errorf("a continue token that never changes must be an error")
	}

	if pages > 3 {
		t.Errorf("callback calls = %d, want the loop cut short", pages)
	}
}

func TestListRejectsUndecodablePage(t *testing.T) {
	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte("not json"))
	}))

	err := newTestClient(t, configFor(server)).List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil })
	if err == nil {
		t.Errorf("a page that is not JSON must be an error")
	}
}
