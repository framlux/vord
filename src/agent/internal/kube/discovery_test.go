// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"context"
	"net/http"
	"reflect"
	"strings"
	"sync"
	"testing"
)

// listedResource is one entry of a discovery document.
type listedResource struct {
	name       string
	kind       string
	namespaced bool
	verbs      []string
}

var allVerbs = []string{"create", "delete", "deletecollection", "get", "list", "patch", "update", "watch"}

// namespacedResource is a resource that can be listed and deleted in a namespace.
func namespacedResource(name string, kind string) listedResource {
	return listedResource{name: name, kind: kind, namespaced: true, verbs: allVerbs}
}

// discoveryDocument renders resources the way the API server answers a request for one group version.
func discoveryDocument(groupVersion string, resources ...listedResource) map[string]any {
	items := make([]any, 0, len(resources))

	for _, resource := range resources {
		items = append(items, map[string]any{
			"name": resource.name, "singularName": "", "namespaced": resource.namespaced, "kind": resource.kind, "verbs": resource.verbs,
		})
	}

	return map[string]any{"kind": "APIResourceList", "groupVersion": groupVersion, "resources": items}
}

// groupEntry renders one group of the /apis document, with its preferred version first.
func groupEntry(name string, versions ...string) map[string]any {
	entries := make([]any, 0, len(versions))

	for _, version := range versions {
		entries = append(entries, map[string]any{"groupVersion": name + "/" + version, "version": version})
	}

	return map[string]any{"name": name, "versions": entries, "preferredVersion": map[string]any{"groupVersion": name + "/" + versions[0], "version": versions[0]}}
}

// discoveryServer answers discovery requests from documents keyed by request path, and records every path asked
// for. A path with no document is a 404, and a path in failures answers with that status.
type discoveryServer struct {
	mu        sync.Mutex
	documents map[string]any
	failures  map[string]int
	requested []string
}

func newDiscoveryServer(t *testing.T) (*discoveryServer, *Client) {
	t.Helper()

	api := &discoveryServer{documents: map[string]any{}, failures: map[string]int{}}

	server := newTLSServer(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		api.mu.Lock()
		defer api.mu.Unlock()

		api.requested = append(api.requested, r.URL.Path)

		if status, failing := api.failures[r.URL.Path]; failing {
			writeStatus(w, status, "ServiceUnavailable", "the server is currently unable to handle the request")

			return
		}

		document, found := api.documents[r.URL.Path]
		if found == false {
			writeStatus(w, http.StatusNotFound, "NotFound", "the server could not find the requested resource")

			return
		}

		writeJSON(w, http.StatusOK, document)
	}))

	return api, newTestClient(t, configFor(server))
}

// resourceNames lists the plural names with their group and version, in the order discovery returned them.
func resourceNames(resources []Resource) []string {
	names := make([]string, 0, len(resources))

	for _, resource := range resources {
		names = append(names, resource.Group+"|"+resource.Version+"|"+resource.Name+"|"+resource.Kind)
	}

	return names
}

// Intent: discovery returns exactly the kinds a namespace holds that can be listed and deleted. A cluster-scoped
// resource, a namespaced one that cannot be listed or cannot be deleted, and a subresource are all left out, and
// the core group and a named group are told apart in the collection path.
func TestDiscoverNamespaced_ReturnsOnlyNamespacedListableDeletableResources(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1",
		namespacedResource("configmaps", "ConfigMap"),
		listedResource{name: "namespaces", kind: "Namespace", namespaced: false, verbs: allVerbs},
		listedResource{name: "nodes", kind: "Node", namespaced: false, verbs: allVerbs},
		listedResource{name: "bindings", kind: "Binding", namespaced: true, verbs: []string{"create"}},
		listedResource{name: "pods/log", kind: "Pod", namespaced: true, verbs: []string{"get"}},
		listedResource{name: "pods/status", kind: "Pod", namespaced: true, verbs: []string{"get", "patch", "update", "list", "delete"}},
		namespacedResource("pods", "Pod"),
	)
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{
		groupEntry("apps", "v1"),
		groupEntry("rbac.authorization.k8s.io", "v1"),
		groupEntry("example.com", "v1"),
	}}
	api.documents["/apis/apps/v1"] = discoveryDocument("apps/v1",
		namespacedResource("deployments", "Deployment"),
		listedResource{name: "deployments/scale", kind: "Scale", namespaced: true, verbs: allVerbs},
	)
	api.documents["/apis/rbac.authorization.k8s.io/v1"] = discoveryDocument("rbac.authorization.k8s.io/v1",
		namespacedResource("roles", "Role"),
		listedResource{name: "clusterroles", kind: "ClusterRole", namespaced: false, verbs: allVerbs},
	)
	api.documents["/apis/example.com/v1"] = discoveryDocument("example.com/v1",
		namespacedResource("widgets", "Widget"),
		listedResource{name: "readonlies", kind: "Readonly", namespaced: true, verbs: []string{"get", "list", "watch"}},
		listedResource{name: "undeletables", kind: "Undeletable", namespaced: true, verbs: []string{"get", "delete"}},
	)

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	want := []string{
		"|v1|configmaps|ConfigMap",
		"|v1|pods|Pod",
		"apps|v1|deployments|Deployment",
		"rbac.authorization.k8s.io|v1|roles|Role",
		"example.com|v1|widgets|Widget",
	}

	if got := resourceNames(discovery.Resources); reflect.DeepEqual(got, want) == false {
		t.Errorf("resources = %v\nwant %v", got, want)
	}

	if len(discovery.Unverified) != 0 {
		t.Errorf("unverified = %v, want none", discovery.Unverified)
	}

	paths := map[string]string{
		"configmaps":  "/api/v1/namespaces/vord-system/configmaps",
		"deployments": "/apis/apps/v1/namespaces/vord-system/deployments",
		"widgets":     "/apis/example.com/v1/namespaces/vord-system/widgets",
	}

	for _, resource := range discovery.Resources {
		if want, listed := paths[resource.Name]; listed && (resource.Collection("vord-system") != want) {
			t.Errorf("%s collection = %q, want %q", resource.Name, resource.Collection("vord-system"), want)
		}
	}
}

// Intent: the metrics groups are never asked for. Their aggregated server answers 503 while it starts and they
// hold no objects, so asking would make a starting cluster look unverifiable.
func TestDiscoverNamespaced_SkipsTheMetricsGroups(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1", namespacedResource("configmaps", "ConfigMap"))
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{
		groupEntry("metrics.k8s.io", "v1beta1"),
		groupEntry("custom.metrics.k8s.io", "v1beta1", "v1beta2"),
		groupEntry("external.metrics.k8s.io", "v1beta1"),
		groupEntry("apps", "v1"),
	}}
	api.documents["/apis/apps/v1"] = discoveryDocument("apps/v1", namespacedResource("deployments", "Deployment"))

	for _, path := range []string{"/apis/metrics.k8s.io/v1beta1", "/apis/custom.metrics.k8s.io/v1beta1", "/apis/custom.metrics.k8s.io/v1beta2", "/apis/external.metrics.k8s.io/v1beta1"} {
		api.failures[path] = http.StatusServiceUnavailable
	}

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	if len(discovery.Unverified) != 0 {
		t.Errorf("unverified = %v, want none: a metrics group is not part of the answer", discovery.Unverified)
	}

	for _, path := range api.requested {
		if strings.Contains(path, "metrics.k8s.io") {
			t.Errorf("discovery asked for %s", path)
		}
	}

	if got, want := len(discovery.Resources), 2; got != want {
		t.Errorf("resources = %v, want the configmaps and deployments", resourceNames(discovery.Resources))
	}
}

// Intent: a group that cannot be read is reported as unverified with its error, and the groups that can be read
// are still returned, so the caller knows exactly what it could not see.
func TestDiscoverNamespaced_UnavailableGroupIsUnverified(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1", namespacedResource("configmaps", "ConfigMap"))
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{
		groupEntry("example.com", "v1"),
		groupEntry("apps", "v1"),
	}}
	api.documents["/apis/apps/v1"] = discoveryDocument("apps/v1", namespacedResource("deployments", "Deployment"))
	api.failures["/apis/example.com/v1"] = http.StatusServiceUnavailable

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	if got, want := resourceNames(discovery.Resources), []string{"|v1|configmaps|ConfigMap", "apps|v1|deployments|Deployment"}; reflect.DeepEqual(got, want) == false {
		t.Errorf("resources = %v, want %v", got, want)
	}

	if len(discovery.Unverified) != 1 {
		t.Fatalf("unverified = %v, want the one group", discovery.Unverified)
	}

	unverified := discovery.Unverified[0]
	if unverified.GroupVersion != "example.com/v1" {
		t.Errorf("unverified group version = %q, want example.com/v1", unverified.GroupVersion)
	}

	if apiError := apiErrorOf(unverified.Err); (apiError == nil) || (apiError.Code != http.StatusServiceUnavailable) {
		t.Errorf("unverified error = %v, want the 503", unverified.Err)
	}
}

// Intent: discovery fails when the core resources or the group list cannot be read, because then nothing can be
// said about the cluster at all.
func TestDiscoverNamespaced_FailsWhenCoreOrGroupListCannotBeRead(t *testing.T) {
	for _, failing := range []string{"/api/v1", "/apis"} {
		t.Run(failing, func(t *testing.T) {
			api, client := newDiscoveryServer(t)

			api.documents["/api/v1"] = discoveryDocument("v1", namespacedResource("configmaps", "ConfigMap"))
			api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{}}
			api.failures[failing] = http.StatusInternalServerError

			discovery, err := client.DiscoverNamespaced(context.Background())
			if err == nil {
				t.Fatalf("DiscoverNamespaced succeeded with %s failing: %v", failing, discovery)
			}

			if apiError := apiErrorOf(err); (apiError == nil) || (apiError.Code != http.StatusInternalServerError) {
				t.Errorf("error = %v, want the 500 in its chain", err)
			}
		})
	}
}

// Intent: two kinds of one group can be served at different versions, as the Gateway API serves TCPRoute at
// v1alpha2 while its other kinds are at v1. Only the preferred version of that group lists the first, so
// discovery reads every version a group serves. A kind served at several versions is returned once, at the
// preferred one.
func TestDiscoverNamespaced_ReadsEveryVersionOfAGroup(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1")
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{
		groupEntry("example.com", "v1", "v1beta1", "v1alpha2"),
	}}
	api.documents["/apis/example.com/v1"] = discoveryDocument("example.com/v1", namespacedResource("gateways", "Gateway"))
	api.documents["/apis/example.com/v1beta1"] = discoveryDocument("example.com/v1beta1", namespacedResource("gateways", "Gateway"), namespacedResource("grants", "Grant"))
	api.documents["/apis/example.com/v1alpha2"] = discoveryDocument("example.com/v1alpha2", namespacedResource("tcproutes", "TCPRoute"))

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	want := []string{
		"example.com|v1|gateways|Gateway",
		"example.com|v1beta1|grants|Grant",
		"example.com|v1alpha2|tcproutes|TCPRoute",
	}

	if got := resourceNames(discovery.Resources); reflect.DeepEqual(got, want) == false {
		t.Errorf("resources = %v\nwant %v", got, want)
	}

	if len(discovery.Unverified) != 0 {
		t.Errorf("unverified = %v, want none", discovery.Unverified)
	}
}

// Intent: a version that is not the preferred one and cannot be read is unverified too, because it may hold kinds
// that no other version lists.
func TestDiscoverNamespaced_UnreadableOlderVersionIsUnverified(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1")
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{groupEntry("example.com", "v1", "v1alpha2")}}
	api.documents["/apis/example.com/v1"] = discoveryDocument("example.com/v1", namespacedResource("gateways", "Gateway"))
	api.failures["/apis/example.com/v1alpha2"] = http.StatusServiceUnavailable

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	if len(discovery.Resources) != 1 {
		t.Errorf("resources = %v, want the gateways", resourceNames(discovery.Resources))
	}

	if (len(discovery.Unverified) != 1) || (discovery.Unverified[0].GroupVersion != "example.com/v1alpha2") {
		t.Errorf("unverified = %v, want example.com/v1alpha2", discovery.Unverified)
	}
}

// Intent: names that discovery returns are put into request paths, so one that is not shaped like a name is never
// requested. The group version or kind it came from is unverified instead of silently dropped.
func TestDiscoverNamespaced_NamesThatAreNotNamesAreNeverRequested(t *testing.T) {
	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1", namespacedResource("configmaps", "ConfigMap"))
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{
		groupEntry("evil/../../v1", "v1"),
		groupEntry("example.com", "v1", "v1?x=1"),
		groupEntry("other.example.com", "v1"),
	}}
	api.documents["/apis/example.com/v1"] = discoveryDocument("example.com/v1", namespacedResource("widgets", "Widget"))
	api.documents["/apis/other.example.com/v1"] = discoveryDocument("other.example.com/v1",
		namespacedResource("Bad Name", "Bad"),
		namespacedResource("good", "Good"),
	)

	discovery, err := client.DiscoverNamespaced(context.Background())
	if err != nil {
		t.Fatalf("DiscoverNamespaced: %v", err)
	}

	for _, path := range api.requested {
		if strings.Contains(path, "..") || strings.Contains(path, "?") {
			t.Errorf("discovery requested %q", path)
		}
	}

	if got, want := resourceNames(discovery.Resources), []string{"|v1|configmaps|ConfigMap", "example.com|v1|widgets|Widget", "other.example.com|v1|good|Good"}; reflect.DeepEqual(got, want) == false {
		t.Errorf("resources = %v, want %v", got, want)
	}

	if len(discovery.Unverified) != 3 {
		t.Errorf("unverified = %v, want the bad group, the bad version and the bad resource name", discovery.Unverified)
	}
}

// Intent: discovery stops when its context ends, rather than reporting every remaining group as unverified.
func TestDiscoverNamespaced_StopsWhenTheContextEnds(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())

	var once sync.Once

	api, client := newDiscoveryServer(t)

	api.documents["/api/v1"] = discoveryDocument("v1")
	api.documents["/apis"] = map[string]any{"kind": "APIGroupList", "groups": []any{groupEntry("example.com", "v1"), groupEntry("apps", "v1")}}
	api.documents["/apis/apps/v1"] = discoveryDocument("apps/v1", namespacedResource("deployments", "Deployment"))
	api.failures["/apis/example.com/v1"] = http.StatusServiceUnavailable

	// The first group's request ends the context as it is answered.
	client.http.Transport = cancellingTransport{inner: client.http.Transport, onPath: "/apis/example.com/v1", cancel: func() { once.Do(cancel) }}

	if _, err := client.DiscoverNamespaced(ctx); err == nil {
		t.Fatal("DiscoverNamespaced succeeded although its context ended")
	}

	for _, path := range api.requested {
		if path == "/apis/apps/v1" {
			t.Error("discovery went on to the next group after its context ended")
		}
	}
}

// cancellingTransport ends a context once the request for onPath has been sent.
type cancellingTransport struct {
	inner  http.RoundTripper
	onPath string
	cancel func()
}

func (c cancellingTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	response, err := c.inner.RoundTrip(r)
	if r.URL.Path == c.onPath {
		c.cancel()
	}

	return response, err
}

// Intent: a resource is named the way kubectl names it, which is what an operator can act on.
func TestResource_Names(t *testing.T) {
	core := Resource{Version: "v1", Name: "configmaps", Kind: "ConfigMap"}
	grouped := Resource{Group: "example.com", Version: "v1", Name: "widgets", Kind: "Widget"}

	if core.String() != "configmaps" || core.KindName() != "ConfigMap" {
		t.Errorf("core resource = %q / %q", core.String(), core.KindName())
	}

	if grouped.String() != "widgets.example.com" || grouped.KindName() != "Widget.example.com" {
		t.Errorf("grouped resource = %q / %q", grouped.String(), grouped.KindName())
	}
}
