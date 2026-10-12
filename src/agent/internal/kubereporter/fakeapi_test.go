// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"sort"
	"strings"
	"sync"
	"testing"
)

// fakeAdminToken is the bearer token the fake API accepts from the admin kubeconfig.
const fakeAdminToken = "admin-token-do-not-leak"

// recordedRequest is one request the fake API served.
type recordedRequest struct {
	Method    string
	Path      string
	RawQuery  string
	Principal string // admin, reporter or none
	Body      map[string]any
	Status    int
}

// fault makes the fake API answer matching requests with a failure instead of its normal behaviour.
type fault struct {
	method string // empty matches any method
	path   string // exact request path
	status int
	reason string
	// message is the Status message; causes are the reasons of details.causes.
	message string
	causes  []string
	// times is how many matching requests fail; -1 means every one.
	times int
	// raceCreate makes a matching POST store its object, as a concurrent creator would have done, and then
	// answer AlreadyExists.
	raceCreate bool
	// onFire runs, with the API locked, when the fault fires.
	onFire func(*fakeAPI)
}

// fakeAPI is an in-memory stand-in for the Kubernetes API server, served over TLS. It holds objects in a map
// keyed by path and supports the verbs the reporter commands use: POST to a collection, GET, PUT with a
// resourceVersion compare-and-swap, DELETE with a uid precondition, and a labelSelector list.
type fakeAPI struct {
	t      *testing.T
	server *httptest.Server
	caPEM  []byte

	mu       sync.Mutex
	objects  map[string]map[string]any
	nextRV   int
	nextUID  int
	nextTok  int
	requests []recordedRequest
	faults   []*fault

	// issued maps a token the fake handed out to the Secret path it lives in. Deleting the Secret revokes it.
	issued map[string]string

	// secretCAPEM, when set, is the ca.crt the fake puts into token Secrets instead of its own certificate.
	secretCAPEM []byte

	// populateAfter is how many GETs of a token Secret see it without data; a negative value never populates it.
	populateAfter int
	secretGets    map[string]int

	// nsGoneAfter is how many GETs of a terminating namespace still find it before it finishes deleting.
	nsGoneAfter int
	nsGets      int
}

// newFakeAPI starts the fake on a TLS test server that closes with the test.
func newFakeAPI(t *testing.T) *fakeAPI {
	t.Helper()

	api := &fakeAPI{
		t:             t,
		objects:       map[string]map[string]any{},
		issued:        map[string]string{},
		secretGets:    map[string]int{},
		populateAfter: 1,
	}

	server := httptest.NewUnstartedServer(http.HandlerFunc(api.serve))
	server.Config.ErrorLog = log.New(io.Discard, "", 0)
	server.StartTLS()
	t.Cleanup(server.Close)

	api.server = server
	api.caPEM = pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: server.Certificate().Raw})

	return api
}

// url is the address of the fake.
func (a *fakeAPI) url() string {
	return a.server.URL
}

// seed stores obj at path as if it had always been there, giving it a uid and resourceVersion.
func (a *fakeAPI) seed(path string, obj map[string]any) {
	a.mu.Lock()
	defer a.mu.Unlock()

	a.storeLocked(path, obj)
}

// get returns a copy of the object at path, or nil.
func (a *fakeAPI) get(path string) map[string]any {
	a.mu.Lock()
	defer a.mu.Unlock()

	obj, found := a.objects[path]
	if found == false {
		return nil
	}

	return cloneObject(obj)
}

// has reports whether an object exists at path.
func (a *fakeAPI) has(path string) bool {
	return a.get(path) != nil
}

// resourceVersion returns the object's resourceVersion, or "" when it is absent.
func (a *fakeAPI) resourceVersion(path string) string {
	return stringAt(a.get(path), "metadata", "resourceVersion")
}

// paths lists the stored object paths under prefix, sorted.
func (a *fakeAPI) paths(prefix string) []string {
	a.mu.Lock()
	defer a.mu.Unlock()

	var out []string

	for path := range a.objects {
		if strings.HasPrefix(path, prefix) {
			out = append(out, path)
		}
	}

	sort.Strings(out)

	return out
}

// inject adds a fault.
func (a *fakeAPI) inject(f fault) {
	a.mu.Lock()
	defer a.mu.Unlock()

	if f.times == 0 {
		f.times = 1
	}

	a.faults = append(a.faults, &f)
}

// requestLog returns a copy of every request served so far.
func (a *fakeAPI) requestLog() []recordedRequest {
	a.mu.Lock()
	defer a.mu.Unlock()

	return append([]recordedRequest(nil), a.requests...)
}

// requestCount returns how many requests so far used method on path.
func (a *fakeAPI) requestCount(method string, path string) int {
	count := 0

	for _, request := range a.requestLog() {
		if (request.Method == method) && (request.Path == path) {
			count++
		}
	}

	return count
}

// writesSince counts the POST, PUT and DELETE requests after the first start entries of the log.
func (a *fakeAPI) writesSince(start int) []recordedRequest {
	var writes []recordedRequest

	for _, request := range a.requestLog()[start:] {
		if request.Method != http.MethodGet {
			writes = append(writes, request)
		}
	}

	return writes
}

// markNamespaceTerminating gives the namespace a deletionTimestamp. It disappears, with everything in it, once
// it has been read goneAfter times.
func (a *fakeAPI) markNamespaceTerminating(goneAfter int) {
	a.mu.Lock()
	defer a.mu.Unlock()

	a.markNamespaceTerminatingLocked(goneAfter)
}

func (a *fakeAPI) markNamespaceTerminatingLocked(goneAfter int) {
	namespace, found := a.objects[namespacePath]
	if found == false {
		a.t.Fatalf("no namespace to mark terminating")
	}

	metadata := namespace["metadata"].(map[string]any)
	metadata["deletionTimestamp"] = "2026-10-11T12:00:00Z"
	namespace["status"] = map[string]any{"phase": "Terminating"}
	a.nsGoneAfter = goneAfter
	a.nsGets = 0
}

// revoked reports whether the token has been handed out and its Secret is gone.
func (a *fakeAPI) issuedTokens() map[string]string {
	a.mu.Lock()
	defer a.mu.Unlock()

	out := map[string]string{}
	for token, path := range a.issued {
		out[token] = path
	}

	return out
}

// storeLocked saves obj, filling in the fields the API server owns.
func (a *fakeAPI) storeLocked(path string, obj map[string]any) {
	metadata, ok := obj["metadata"].(map[string]any)
	if ok == false {
		metadata = map[string]any{}
		obj["metadata"] = metadata
	}

	a.nextRV++
	metadata["resourceVersion"] = fmt.Sprintf("%d", a.nextRV)

	if _, hasUID := metadata["uid"]; hasUID == false {
		a.nextUID++
		metadata["uid"] = fmt.Sprintf("uid-%d", a.nextUID)
		metadata["creationTimestamp"] = "2026-10-11T12:00:00Z"
	}

	a.objects[path] = obj
}

// serve handles one request.
func (a *fakeAPI) serve(w http.ResponseWriter, r *http.Request) {
	a.mu.Lock()
	defer a.mu.Unlock()

	var body map[string]any

	raw, _ := io.ReadAll(r.Body)
	if len(raw) > 0 {
		_ = json.Unmarshal(raw, &body)
	}

	principal := "none"

	switch token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "); {
	case token == fakeAdminToken:
		principal = "admin"
	case a.issued[token] != "":
		principal = "reporter"
	}

	record := &recordedRequest{Method: r.Method, Path: r.URL.Path, RawQuery: r.URL.RawQuery, Principal: principal, Body: body}
	a.requests = append(a.requests, *record)
	index := len(a.requests) - 1

	finish := func(status int) { a.requests[index].Status = status }

	reply := func(status int, value any) {
		finish(status)
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)

		if err := json.NewEncoder(w).Encode(value); err != nil {
			panic(err)
		}
	}

	fail := func(status int, reason string, message string, causes ...string) {
		details := map[string]any{}

		if len(causes) > 0 {
			list := make([]any, 0, len(causes))
			for _, cause := range causes {
				list = append(list, map[string]any{"reason": cause, "message": message})
			}

			details["causes"] = list
		}

		reply(status, map[string]any{
			"kind": "Status", "apiVersion": "v1", "status": "Failure",
			"message": message, "reason": reason, "code": status, "details": details,
		})
	}

	if principal == "none" {
		fail(http.StatusUnauthorized, "Unauthorized", "Unauthorized")

		return
	}

	for _, f := range a.faults {
		if (f.times == 0) || ((f.method != "") && (f.method != r.Method)) || (f.path != r.URL.Path) {
			continue
		}

		if f.times > 0 {
			f.times--
		}

		if f.onFire != nil {
			f.onFire(a)
		}

		if f.raceCreate {
			if body != nil {
				a.storeLocked(r.URL.Path+"/"+stringAt(body, "metadata", "name"), body)
			}

			fail(http.StatusConflict, "AlreadyExists", "already exists")

			return
		}

		fail(f.status, f.reason, f.message, f.causes...)

		return
	}

	if principal == "reporter" {
		if (r.Method == http.MethodGet) && (r.URL.Path == "/api/v1/nodes") {
			reply(http.StatusOK, map[string]any{"kind": "NodeList", "items": []any{}})
		} else {
			fail(http.StatusForbidden, "Forbidden", "the reporter token may only list nodes")
		}

		return
	}

	switch r.Method {
	case http.MethodGet:
		a.handleGet(r, reply, fail)
	case http.MethodPost:
		a.handlePost(r, body, reply, fail)
	case http.MethodPut:
		a.handlePut(r, body, reply, fail)
	case http.MethodDelete:
		a.handleDelete(r, body, reply, fail)
	default:
		fail(http.StatusMethodNotAllowed, "MethodNotAllowed", "method not allowed")
	}
}

type replyFunc func(status int, value any)

type failFunc func(status int, reason string, message string, causes ...string)

func (a *fakeAPI) handleGet(r *http.Request, reply replyFunc, fail failFunc) {
	path := r.URL.Path

	if path == namespacePath {
		if stringAt(a.objects[path], "metadata", "deletionTimestamp") != "" {
			a.nsGets++

			if a.nsGets > a.nsGoneAfter {
				a.deleteLocked(path)
				fail(http.StatusNotFound, "NotFound", "namespaces \"vord-system\" not found")

				return
			}
		}
	}

	if obj, found := a.objects[path]; found {
		if strings.Contains(path, "/secrets/"+tokenSecretPrefix) {
			a.populateLocked(path, obj)
			obj = a.objects[path]
		}

		reply(http.StatusOK, obj)

		return
	}

	if strings.HasSuffix(path, "/secrets") {
		a.listSecrets(r, path, reply)

		return
	}

	fail(http.StatusNotFound, "NotFound", "the server could not find the requested resource")
}

// listSecrets answers a labelSelector list of the Secrets in a namespace.
func (a *fakeAPI) listSecrets(r *http.Request, collection string, reply replyFunc) {
	selector := r.URL.Query().Get("labelSelector")
	items := []any{}

	var names []string

	for path := range a.objects {
		if strings.HasPrefix(path, collection+"/") {
			names = append(names, path)
		}
	}

	sort.Strings(names)

	for _, path := range names {
		obj := a.objects[path]

		if selector != "" {
			key, value, _ := strings.Cut(selector, "=")
			if stringAt(obj, "metadata", "labels", key) != value {
				continue
			}
		}

		items = append(items, obj)
	}

	reply(http.StatusOK, map[string]any{
		"kind": "SecretList", "apiVersion": "v1", "metadata": map[string]any{"resourceVersion": "1"}, "items": items,
	})
}

// populateLocked fills a service-account token Secret with data once it has been read populateAfter times.
func (a *fakeAPI) populateLocked(path string, obj map[string]any) {
	if (stringAt(obj, "type") != serviceAccountTokenType) || (a.populateAfter < 0) {
		return
	}

	if _, populated := obj["data"]; populated {
		return
	}

	a.secretGets[path]++

	if a.secretGets[path] <= a.populateAfter {
		return
	}

	caPEM := a.caPEM
	if a.secretCAPEM != nil {
		caPEM = a.secretCAPEM
	}

	a.nextTok++
	token := fmt.Sprintf("reporter-token-%d-do-not-leak", a.nextTok)
	a.issued[token] = path
	obj["data"] = map[string]any{
		"token":     base64.StdEncoding.EncodeToString([]byte(token)),
		"ca.crt":    base64.StdEncoding.EncodeToString(caPEM),
		"namespace": base64.StdEncoding.EncodeToString([]byte(reporterNamespace)),
	}
}

func (a *fakeAPI) handlePost(r *http.Request, body map[string]any, reply replyFunc, fail failFunc) {
	collection := r.URL.Path

	name := stringAt(body, "metadata", "name")
	if name == "" {
		fail(http.StatusUnprocessableEntity, "Invalid", "metadata.name is required")

		return
	}

	if strings.Contains(collection, "/namespaces/") {
		namespace := strings.SplitN(strings.SplitN(collection, "/namespaces/", 2)[1], "/", 2)[0]
		ns, found := a.objects["/api/v1/namespaces/"+namespace]

		switch {
		case found == false:
			fail(http.StatusNotFound, "NotFound", fmt.Sprintf("namespaces %q not found", namespace))

			return
		case stringAt(ns, "metadata", "deletionTimestamp") != "":
			fail(http.StatusForbidden, "Forbidden",
				fmt.Sprintf("unable to create new content in namespace %s because it is being terminated", namespace),
				"NamespaceTerminating")

			return
		}
	}

	path := collection + "/" + name
	if _, exists := a.objects[path]; exists {
		fail(http.StatusConflict, "AlreadyExists", fmt.Sprintf("%q already exists", name))

		return
	}

	a.storeLocked(path, body)
	reply(http.StatusCreated, a.objects[path])
}

func (a *fakeAPI) handlePut(r *http.Request, body map[string]any, reply replyFunc, fail failFunc) {
	path := r.URL.Path

	current, found := a.objects[path]
	if found == false {
		fail(http.StatusNotFound, "NotFound", "not found")

		return
	}

	if sent := stringAt(body, "metadata", "resourceVersion"); (sent != "") && (sent != stringAt(current, "metadata", "resourceVersion")) {
		fail(http.StatusConflict, "Conflict", "the object has been modified; please apply your changes to the latest version and try again")

		return
	}

	metadata, ok := body["metadata"].(map[string]any)
	if ok == false {
		metadata = map[string]any{}
		body["metadata"] = metadata
	}

	currentMetadata := current["metadata"].(map[string]any)
	metadata["uid"] = currentMetadata["uid"]
	metadata["creationTimestamp"] = currentMetadata["creationTimestamp"]

	a.storeLocked(path, body)
	reply(http.StatusOK, a.objects[path])
}

func (a *fakeAPI) handleDelete(r *http.Request, body map[string]any, reply replyFunc, fail failFunc) {
	path := r.URL.Path

	current, found := a.objects[path]
	if found == false {
		fail(http.StatusNotFound, "NotFound", "not found")

		return
	}

	if want := stringAt(body, "preconditions", "uid"); (want != "") && (want != stringAt(current, "metadata", "uid")) {
		fail(http.StatusConflict, "Conflict", "Precondition failed: UID in precondition does not match UID in record")

		return
	}

	a.deleteLocked(path)
	reply(http.StatusOK, map[string]any{"kind": "Status", "apiVersion": "v1", "status": "Success"})
}

// deleteLocked removes an object. Removing a namespace removes everything in it, and removing a token Secret
// revokes its token.
func (a *fakeAPI) deleteLocked(path string) {
	delete(a.objects, path)

	for token, secretPath := range a.issued {
		if secretPath == path {
			delete(a.issued, token)
		}
	}

	if path != namespacePath {
		return
	}

	for other := range a.objects {
		if strings.Contains(other, "/namespaces/"+reporterNamespace+"/") {
			a.deleteLocked(other)
		}
	}
}

// cloneObject deep-copies a decoded JSON object.
func cloneObject(obj map[string]any) map[string]any {
	encoded, err := json.Marshal(obj)
	if err != nil {
		panic(err)
	}

	var out map[string]any
	if err := json.Unmarshal(encoded, &out); err != nil {
		panic(err)
	}

	return out
}
