// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kube

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"regexp"
	"slices"
	"strings"
)

// skippedDiscoveryGroups are the API groups discovery does not look into. They are read-only views of metrics
// that hold no objects, and the aggregated server behind them answers 503 for a while after the cluster starts,
// which would otherwise make every cluster look unverifiable at that moment.
var skippedDiscoveryGroups = []string{"metrics.k8s.io", "custom.metrics.k8s.io", "external.metrics.k8s.io"}

// namePattern is what a group, version, resource or object name looks like.
var namePattern = regexp.MustCompile(`^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$`)

// IsValidName reports whether name is shaped like a Kubernetes name. A name the cluster returns is put into a
// request path, so one that is not shaped like a name is not trusted.
func IsValidName(name string) bool {
	return namePattern.MatchString(name)
}

// Resource is one namespaced kind the API serves and that can be listed and deleted.
type Resource struct {
	// Group is the API group, empty for the core group.
	Group string

	// Version is the API version the resource was found under.
	Version string

	// Name is the plural name used in request paths, such as "deployments".
	Name string

	// Kind is the singular kind, such as "Deployment". It is for display only.
	Kind string
}

// Collection returns the path that lists the resource in namespace.
func (r Resource) Collection(namespace string) string {
	if r.Group == "" {
		return "/api/" + r.Version + "/namespaces/" + namespace + "/" + r.Name
	}

	return "/apis/" + r.Group + "/" + r.Version + "/namespaces/" + namespace + "/" + r.Name
}

// String names the resource the way kubectl does: its plural name, followed by the group when it has one.
func (r Resource) String() string {
	if r.Group == "" {
		return r.Name
	}

	return r.Name + "." + r.Group
}

// KindName names the kind the way kubectl does: the kind, followed by the group when it has one.
func (r Resource) KindName() string {
	if r.Group == "" {
		return r.Kind
	}

	return r.Kind + "." + r.Group
}

// UnverifiedGroup is a group version whose resources could not be read, with the reason.
type UnverifiedGroup struct {
	// GroupVersion is "v1" for the core group and "group/version" for the others.
	GroupVersion string

	// Err is why its resources could not be read.
	Err error
}

// Discovery is what the API server says it serves.
type Discovery struct {
	// Resources are the namespaced kinds that have both the list and delete verbs and are not subresources,
	// each listed once.
	Resources []Resource

	// Unverified are the group versions whose resource list failed or could not be trusted. Kinds they hold are
	// not in Resources, so a caller that needs every kind must treat them as unknown.
	Unverified []UnverifiedGroup
}

type resourceList struct {
	Resources []struct {
		Name       string   `json:"name"`
		Kind       string   `json:"kind"`
		Namespaced bool     `json:"namespaced"`
		Verbs      []string `json:"verbs"`
	} `json:"resources"`
}

// groupVersionEntry is how a group document names one version.
type groupVersionEntry struct {
	Version string `json:"version"`
}

type groupList struct {
	Groups []struct {
		Name             string              `json:"name"`
		Versions         []groupVersionEntry `json:"versions"`
		PreferredVersion groupVersionEntry   `json:"preferredVersion"`
	} `json:"groups"`
}

// DiscoverNamespaced asks the API server which namespaced kinds it serves that can be listed and deleted. It
// reads the core group at /api/v1, the groups at /apis, and then the resources of every version each group
// serves, the preferred one first: two kinds of one group can be served at different versions, so the preferred
// version alone would miss one. A kind served at several versions is returned once, at the first.
//
// The groups metrics.k8s.io, custom.metrics.k8s.io and external.metrics.k8s.io are skipped. A group version whose
// resources cannot be read is returned in Unverified with the error, and discovery carries on with the rest.
// DiscoverNamespaced itself fails only when /api/v1 or /apis cannot be read, or when ctx ends.
func (c *Client) DiscoverNamespaced(ctx context.Context) (Discovery, error) {
	var result Discovery

	var core resourceList
	if err := c.Do(ctx, http.MethodGet, "/api/v1", nil, nil, &core); err != nil {
		return Discovery{}, fmt.Errorf("discover the core resources: %w", err)
	}

	result.Resources, result.Unverified = usableResources("", "v1", core)

	var groups groupList
	if err := c.Do(ctx, http.MethodGet, "/apis", nil, nil, &groups); err != nil {
		return Discovery{}, fmt.Errorf("discover the API groups: %w", err)
	}

	for _, group := range groups.Groups {
		if slices.Contains(skippedDiscoveryGroups, group.Name) {
			continue
		}

		if IsValidName(group.Name) == false {
			result.Unverified = append(result.Unverified, UnverifiedGroup{GroupVersion: sanitizeText(group.Name), Err: errors.New("its name is not a valid group name")})

			continue
		}

		seen := map[string]bool{}

		for _, version := range orderedVersions(group.PreferredVersion.Version, group.Versions) {
			groupVersion := group.Name + "/" + version

			if IsValidName(version) == false {
				result.Unverified = append(result.Unverified, UnverifiedGroup{GroupVersion: sanitizeText(groupVersion), Err: errors.New("its version is not a valid version name")})

				continue
			}

			var list resourceList
			if err := c.Do(ctx, http.MethodGet, "/apis/"+groupVersion, nil, nil, &list); err != nil {
				if ctx.Err() != nil {
					return Discovery{}, ctx.Err()
				}

				result.Unverified = append(result.Unverified, UnverifiedGroup{GroupVersion: groupVersion, Err: err})

				continue
			}

			usable, unverified := usableResources(group.Name, version, list)

			result.Unverified = append(result.Unverified, unverified...)

			for _, resource := range usable {
				if seen[resource.Name] == false {
					seen[resource.Name] = true
					result.Resources = append(result.Resources, resource)
				}
			}
		}
	}

	return result, nil
}

// orderedVersions returns the preferred version followed by the others, each once.
func orderedVersions(preferred string, versions []groupVersionEntry) []string {
	var ordered []string

	if preferred != "" {
		ordered = append(ordered, preferred)
	}

	for _, entry := range versions {
		if (entry.Version != "") && (slices.Contains(ordered, entry.Version) == false) {
			ordered = append(ordered, entry.Version)
		}
	}

	return ordered
}

// usableResources returns the resources of one group version that can be listed and deleted in a namespace. A
// namespaced resource that can be deleted but not listed, and one whose name cannot go into a path, are returned
// as unverified instead: deleting the namespace would destroy their objects, and they cannot be looked at.
func usableResources(group string, version string, list resourceList) ([]Resource, []UnverifiedGroup) {
	var resources []Resource

	var unverified []UnverifiedGroup

	groupVersion := version
	if group != "" {
		groupVersion = group + "/" + version
	}

	for _, entry := range list.Resources {
		// A subresource is named after its parent, with a slash.
		if (entry.Namespaced == false) || strings.Contains(entry.Name, "/") || (slices.Contains(entry.Verbs, "delete") == false) {
			continue
		}

		if IsValidName(entry.Name) == false {
			unverified = append(unverified, UnverifiedGroup{GroupVersion: groupVersion, Err: errors.New("it serves a resource whose name is not a valid resource name")})

			continue
		}

		if slices.Contains(entry.Verbs, "list") == false {
			unverified = append(unverified, UnverifiedGroup{GroupVersion: groupVersion, Err: fmt.Errorf("it serves %s, which can be deleted but not listed", entry.Name)})

			continue
		}

		resources = append(resources, Resource{Group: group, Version: version, Name: entry.Name, Kind: sanitizeText(entry.Kind)})
	}

	return resources, unverified
}
