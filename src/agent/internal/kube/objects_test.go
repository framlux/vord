// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"bytes"
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
	"sync/atomic"
	"testing"
)

const leasePath = "/apis/coordination.k8s.io/v1/namespaces/vord-system/leases/vord-reporter"

// decodeExact decodes JSON the way GetRaw does, keeping numbers exact.
func decodeExact(t *testing.T, data []byte) map[string]any {
	t.Helper()

	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()

	var obj map[string]any
	if err := decoder.Decode(&obj); err != nil {
		t.Fatalf("decode %s: %v", data, err)
	}

	return obj
}

const storedLease = `{
  "apiVersion": "coordination.k8s.io/v1",
  "kind": "Lease",
  "metadata": {
    "name": "vord-reporter",
    "namespace": "vord-system",
    "resourceVersion": "41",
    "generation": 9007199254740993,
    "annotations": {"vord.framlux.io/role-version": "1", "example.com/keep": "me"}
  },
  "spec": {
    "holderIdentity": "vord-old",
    "leaseDurationSeconds": 60,
    "leaseTransitions": 7,
    "strategy": "OldestEmulationVersion",
    "preferredHolder": "vord-other",
    "futureField": {"nested": ["a", 1, null, true, 1.5, 12345678901234567890]}
  }
}`

// Intent: read-modify-write through GetRaw and PutRaw keeps every field the agent does not know, and every number
// exactly. The server stands in for an API version newer than the agent, and what it stored must come back
// unchanged except for the field the agent edited, including an integer above 2^53 that a float64 would round.
func TestRawReadModifyWritePreservesUnknownFields(t *testing.T) {
	var mu sync.Mutex

	var received []byte

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()

		switch r.Method {
		case http.MethodGet:
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(storedLease))
		case http.MethodPut:
			received, _ = io.ReadAll(r.Body)

			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write(received)
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
	spec["leaseTransitions"] = int64(8)

	updated, err := client.PutRaw(context.Background(), leasePath, lease)
	if err != nil {
		t.Fatalf("PutRaw: %v", err)
	}

	want := decodeExact(t, []byte(storedLease))
	wantSpec := want["spec"].(map[string]any)
	wantSpec["holderIdentity"] = "vord-new"
	wantSpec["leaseTransitions"] = json.Number("8")

	if got := decodeExact(t, received); reflect.DeepEqual(got, want) == false {
		t.Errorf("the PUT body lost or changed fields:\n got %v\nwant %v", got, want)
	}

	if bytes.Contains(received, []byte("9007199254740993")) == false {
		t.Errorf("the integer above 2^53 did not survive the round trip: %s", received)
	}

	if bytes.Contains(received, []byte("12345678901234567890")) == false {
		t.Errorf("the integer above 2^63 did not survive the round trip: %s", received)
	}

	if bytes.Contains(received, []byte(`"strategy":"OldestEmulationVersion"`)) == false {
		t.Errorf("spec.strategy was dropped: %s", received)
	}

	if reflect.DeepEqual(updated, want) == false {
		t.Errorf("PutRaw returned %v, want the stored object %v", updated, want)
	}

	generation := lease["metadata"].(map[string]any)["generation"]
	if generation != json.Number("9007199254740993") {
		t.Errorf("generation = %#v, want json.Number 9007199254740993", generation)
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
	}, nil)
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

	if err := client.List(context.Background(), "/api/v1/nodes", nil, 0, func([]byte) error { return nil }, nil); err != nil {
		t.Fatalf("List: %v", err)
	}

	if _, present := script.queries[0]["limit"]; present {
		t.Errorf("limit was sent although none was asked for: %v", script.queries[0])
	}
}

// Intent: when the continue token expires (410) on page two, List starts again from page one and finishes. The
// callback receives page one twice, the second time after the restart.
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

	if err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil }, nil); err != nil {
		t.Fatalf("List: %v", err)
	}

	if pages != 4 {
		t.Errorf("callback calls = %d, want 4 (page one twice, then pages two and three)", pages)
	}

	if len(script.queries) != 5 {
		t.Errorf("server saw %d requests, want 5", len(script.queries))
	}
}

// itemsOf returns the items of a page body made by pageBody.
func itemsOf(t *testing.T, body []byte) []string {
	t.Helper()

	var parsed struct {
		Items []string `json:"items"`
	}

	if err := json.Unmarshal(body, &parsed); err != nil {
		t.Fatalf("decode page %s: %v", body, err)
	}

	return parsed.Items
}

// Intent: a restart after page two is announced exactly once, before page one is delivered again, so a caller that
// accumulates can reset and still ends with the totals of the one complete pass.
func TestListAnnouncesRestartBeforeRedeliveringPageOne(t *testing.T) {
	script := &listServer{steps: []listStep{
		{body: pageBody("t1", "a")},
		{body: pageBody("t2", "b"), wantTok: "t1"},
		{status: http.StatusGone, reason: "Expired", wantTok: "t2"},
		{body: pageBody("n1", "a")},
		{body: pageBody("n2", "b"), wantTok: "n1"},
		{body: pageBody("", "c"), wantTok: "n2"},
	}}

	client := newTestClient(t, configFor(newTLSServer(t, script)))

	var accumulated, events []string

	err := client.List(context.Background(), "/api/v1/pods", nil, 1,
		func(body []byte) error {
			accumulated = append(accumulated, itemsOf(t, body)...)
			events = append(events, "page")

			return nil
		},
		func() {
			accumulated = nil
			events = append(events, "restart")
		})
	if err != nil {
		t.Fatalf("List: %v", err)
	}

	if want := []string{"page", "page", "restart", "page", "page", "page"}; reflect.DeepEqual(events, want) == false {
		t.Errorf("events = %v, want %v", events, want)
	}

	if want := []string{"a", "b", "c"}; reflect.DeepEqual(accumulated, want) == false {
		t.Errorf("accumulated = %v, want %v with no repeats from the abandoned pass", accumulated, want)
	}
}

// Intent: onRestart is optional, and it is not called when nothing was restarted.
func TestListDoesNotCallOnRestartWithoutRestart(t *testing.T) {
	script := &listServer{steps: []listStep{{body: pageBody("t1", "a")}, {body: pageBody("", "b"), wantTok: "t1"}}}
	client := newTestClient(t, configFor(newTLSServer(t, script)))

	var restarts int

	if err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil }, func() { restarts++ }); err != nil {
		t.Fatalf("List: %v", err)
	}

	if restarts != 0 {
		t.Errorf("onRestart calls = %d, want 0", restarts)
	}
}

// Intent: a list is bounded in pages as well as by the caller's context: a server that never ends the list is cut
// off, and a list of exactly the maximum still succeeds.
func TestListStopsAfterMaximumPages(t *testing.T) {
	var requests atomic.Int32

	endless := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(pageBody("token-" + strconv.Itoa(int(requests.Add(1))))))
	}))

	client := newTestClient(t, configFor(endless))
	client.maxPages = 3

	var pages int

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil }, nil)
	if err == nil {
		t.Fatalf("an endless list must be cut off")
	}

	if apiErrorOf(err) != nil {
		t.Errorf("err = %v, want a local error, not an API error", err)
	}

	if (pages != 3) || (requests.Load() != 3) {
		t.Errorf("pages = %d, requests = %d, want 3 and 3", pages, requests.Load())
	}

	exact := &listServer{steps: []listStep{{body: pageBody("t1")}, {body: pageBody("t2"), wantTok: "t1"}, {body: pageBody(""), wantTok: "t2"}}}
	client = newTestClient(t, configFor(newTLSServer(t, exact)))
	client.maxPages = 3

	if err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil }, nil); err != nil {
		t.Errorf("a list of exactly the maximum pages failed: %v", err)
	}

	if newTestClient(t, configFor(endless)).maxPages != 10_000 {
		t.Errorf("the default page limit is not 10,000")
	}
}

// Intent: restarts do not reset the page budget, so a server that keeps expiring tokens cannot extend the work.
func TestListPageBudgetSpansRestarts(t *testing.T) {
	script := &listServer{steps: []listStep{
		{body: pageBody("t1")},
		{body: pageBody("t2"), wantTok: "t1"},
		{status: http.StatusGone, reason: "Expired", wantTok: "t2"},
		{body: pageBody("n1")},
		{body: pageBody("n2"), wantTok: "n1"},
		{body: pageBody(""), wantTok: "n2"},
	}}

	client := newTestClient(t, configFor(newTLSServer(t, script)))
	client.maxPages = 3

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil }, nil)
	if err == nil {
		t.Errorf("pages of an abandoned pass must count against the budget")
	}

	if len(script.queries) != 4 {
		t.Errorf("server saw %d requests, want 4 (two pages, the 410, then one page before the budget ran out)", len(script.queries))
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

	var pages, restarts int

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil }, func() { restarts++ })

	apiError := apiErrorOf(err)
	if (apiError == nil) || (apiError.Code != http.StatusGone) {
		t.Errorf("err = %v, want the 410", err)
	}

	if restarts != 3 {
		t.Errorf("onRestart calls = %d, want 3", restarts)
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

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil }, nil)
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

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return stop }, nil)
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

	err := client.List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { return nil }, nil)
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

	err := newTestClient(t, configFor(server)).List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { pages++; return nil }, nil)
	if err == nil {
		t.Errorf("a continue token that never changes must be an error")
	}

	if pages > 3 {
		t.Errorf("callback calls = %d, want the loop cut short", pages)
	}
}

// Intent: a page that is not valid JSON is rejected before the callback sees it.
func TestListRejectsUndecodablePageBeforeDeliveringIt(t *testing.T) {
	for _, body := range []string{"not json", "[1]", `{"metadata":`, `"text"`} {
		server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			_, _ = w.Write([]byte(body))
		}))

		var delivered int

		err := newTestClient(t, configFor(server)).List(context.Background(), "/api/v1/pods", nil, 1, func([]byte) error { delivered++; return nil }, nil)
		if err == nil {
			t.Errorf("body %q: a page that is not JSON must be an error", body)
		}

		if delivered != 0 {
			t.Errorf("body %q: the callback received the undecodable page %d times", body, delivered)
		}
	}
}
