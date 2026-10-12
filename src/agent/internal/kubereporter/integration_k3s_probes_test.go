// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

//go:build k3s_integration

package kubereporter_test

import (
	"context"
	"errors"
	"net/http"
	"testing"
	"time"

	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubereporter"
)

// staleLeaseWrite replaces the Lease with the reporter's token from a stale resourceVersion, which is what the
// election's compare-and-swap relies on the cluster to refuse.
func (s *scenario) staleLeaseWrite(t *testing.T) {
	ctx := context.Background()
	client := newReporterClient(t, s.holder.state(t))

	original, err := client.GetRaw(ctx, leasePath)
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	fresh := deepCopy(t, original)
	annotate(fresh, "vord.framlux.io/test-touch", "fresh")

	if _, err := client.PutRaw(ctx, leasePath, fresh); err != nil {
		t.Fatalf("write the Lease from its current resourceVersion: %v", err)
	}

	stale := deepCopy(t, original)
	annotate(stale, "vord.framlux.io/test-touch", "stale")

	_, err = client.PutRaw(ctx, leasePath, stale)
	if err == nil {
		t.Fatal("the cluster accepted a write from a stale resourceVersion")
	}

	var apiError *kube.APIError
	if errors.As(err, &apiError) == false {
		t.Fatalf("the stale write gave %v, want an API error", err)
	}

	if (apiError.Code != http.StatusConflict) || (apiError.Reason != "Conflict") {
		t.Errorf("a stale Lease write answers %d %q, want 409 Conflict", apiError.Code, apiError.Reason)
	}

	if (kube.IsConflict(err) == false) || kube.IsAlreadyExists(err) || kube.IsAuthRejected(err) || kube.IsNotFound(err) {
		t.Errorf("classification of the stale write: conflict=%v already_exists=%v auth_rejected=%v not_found=%v",
			kube.IsConflict(err), kube.IsAlreadyExists(err), kube.IsAuthRejected(err), kube.IsNotFound(err))
	}

	t.Logf("api fact: a stale-resourceVersion Lease PUT answers HTTP %d reason %q (%s)", apiError.Code, apiError.Reason, apiError.Message)
}

// deletePreconditions deletes objects of the kinds uninstall deletes with a uid precondition: the right uid
// deletes, a wrong or stale one is a Conflict that leaves the object alone.
func (s *scenario) deletePreconditions(t *testing.T) {
	cases := []struct {
		kind       string
		collection string
		manifest   func() map[string]any
	}{
		{
			kind:       "Namespace",
			collection: namespacesPath,
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "v1", "kind": "Namespace", "metadata": map[string]any{"name": "vord-it-precondition"}}
			},
		},
		{
			kind:       "Secret",
			collection: "/api/v1/namespaces/default/secrets",
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "v1", "kind": "Secret", "metadata": map[string]any{"name": "vord-it-precondition"}, "type": "Opaque"}
			},
		},
		{
			kind:       "Lease",
			collection: "/apis/coordination.k8s.io/v1/namespaces/default/leases",
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "coordination.k8s.io/v1", "kind": "Lease", "metadata": map[string]any{"name": "vord-it-precondition"}, "spec": map[string]any{}}
			},
		},
		{
			kind:       "ClusterRole",
			collection: clusterRolesPath,
			manifest: func() map[string]any {
				return map[string]any{"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRole", "metadata": map[string]any{"name": "vord-it-precondition"}, "rules": []any{}}
			},
		},
	}

	for _, c := range cases {
		t.Run(c.kind, func(t *testing.T) {
			s.cluster.exercisePrecondition(t, c.kind, c.collection, c.manifest)
		})
	}
}

// missingLease deletes the Lease while the reporter's account and roles still exist. The reporter must get a 404
// for reading the missing Lease, a Conflict for writing it back at the resourceVersion it read, and a 403 only
// for a Lease it has no rule for, and the holder's elector must stop leading without calling that a credential
// problem.
func (s *scenario) missingLease(t *testing.T) {
	ctx := context.Background()
	holder, standby := s.holder, s.standby
	client := newReporterClient(t, holder.state(t))

	current, err := client.GetRaw(ctx, leasePath)
	if err != nil {
		t.Fatalf("read the Lease: %v", err)
	}

	s.cluster.deleteObject(t, leasePath)

	anonymous := deepCopy(t, current)
	delete(anonymous["metadata"].(map[string]any), "resourceVersion")
	delete(anonymous["metadata"].(map[string]any), "uid")

	_, getErr := client.GetRaw(ctx, leasePath)
	_, putErr := client.PutRaw(ctx, leasePath, current)
	_, bareErr := client.PutRaw(ctx, leasePath, anonymous)
	_, otherErr := client.GetRaw(ctx, leasesPath+"/some-other-lease")
	_, listErr := listWith(ctx, client, leasesPath)

	expectStatus(t, "GET of the missing Lease", getErr, http.StatusNotFound, "NotFound")
	expectStatus(t, "PUT of the missing Lease with the resourceVersion it was read at", putErr, http.StatusConflict, "Conflict")
	expectStatus(t, "GET of another Lease name", otherErr, http.StatusForbidden, "Forbidden")
	expectStatus(t, "LIST of Leases", listErr, http.StatusForbidden, "Forbidden")

	if kube.IsAuthRejected(getErr) || (kube.IsNotFound(getErr) == false) {
		t.Errorf("classification of the missing Lease: auth_rejected=%v not_found=%v", kube.IsAuthRejected(getErr), kube.IsNotFound(getErr))
	}

	if kube.IsAuthRejected(otherErr) == false {
		t.Error("a 403 for a Lease the reporter has no rule for is not classified as rejected credentials")
	}

	t.Logf("api fact: for a missing Lease the reporter's GET answers %s, a PUT of the object it read (resourceVersion and uid) answers %s and a PUT with neither answers %s; a Lease name its Role does not name answers %s",
		statusOf(getErr), statusOf(putErr), statusOf(bareErr), statusOf(otherErr))

	lostBefore := holder.lost.Load()

	holder.tick(t)

	eventually(t, stepTimeout, "the holder to stop leading once the Lease is gone", func() bool { return holder.lost.Load() > lostBefore })

	standby.tick(t)
	standby.tick(t)

	for _, c := range []*candidate{holder, standby} {
		if got := c.runtime.ReporterError(); got != "" {
			t.Errorf("%s reports %q for a missing Lease, which is no credential problem", c.name, got)
		}
	}
}

// terminatingNamespace keeps the namespace in Terminating with a labelled object that holds a finalizer, then
// checks what the real API says to deletes and creates in it and how uninstall and enrol take it.
func (s *scenario) terminatingNamespace(t *testing.T) {
	ctx := context.Background()
	admin := s.cluster.admin

	hold := map[string]any{
		"apiVersion": "v1",
		"kind":       "ConfigMap",
		"metadata": map[string]any{
			"name":       "vord-it-hold",
			"namespace":  reporterNamespace,
			"labels":     map[string]any{managedByLabel: managedByValue},
			"finalizers": []any{"vord.framlux.io/test-hold"},
		},
	}

	if err := admin.Do(ctx, http.MethodPost, configMapsPath, nil, hold, nil); err != nil {
		t.Fatalf("create the object that holds the namespace: %v", err)
	}

	result := s.a.uninstall(t)
	if (result.Success == false) || (result.Message != "uninstalled") {
		t.Fatalf("uninstall with a held object: %+v", result)
	}

	namespace := s.cluster.mustGet(t, namespacePath)
	if stringAt(namespace, "metadata", "deletionTimestamp") == "" {
		t.Fatalf("the namespace is not terminating after uninstall: %v", namespace["status"])
	}

	uid := stringAt(namespace, "metadata", "uid")

	// A delete of a namespace that is already terminating, with the uid precondition uninstall sends.
	status, body := s.cluster.raw(t, http.MethodDelete, namespacePath, deleteOptions(uid))
	if status != http.StatusOK {
		t.Errorf("DELETE of a terminating namespace answers %d, want 200: %s", status, body)
	}

	t.Logf("api fact: DELETE of an already-terminating namespace with a uid precondition answers HTTP %d (kind %v, deletionTimestamp %v)",
		status, body["kind"], nestedValue(body, "metadata", "deletionTimestamp"))

	plainStatus, _ := s.cluster.raw(t, http.MethodDelete, namespacePath, nil)
	staleStatus, staleBody := s.cluster.raw(t, http.MethodDelete, namespacePath, deleteOptions("00000000-0000-0000-0000-000000000000"))

	t.Logf("api fact: DELETE of a terminating namespace without a body answers HTTP %d; with a wrong uid HTTP %d %v", plainStatus, staleStatus, staleBody["reason"])

	// Uninstall run again on every host while the namespace is terminating must still finish.
	for _, c := range []*candidate{s.b, s.a} {
		again := c.uninstall(t)
		if (again.Success == false) || (again.Message != "uninstalled") {
			t.Errorf("%s: uninstall on a terminating namespace = %+v", c.name, again)
		}
	}

	if stringAt(s.cluster.mustGet(t, namespacePath), "metadata", "deletionTimestamp") == "" {
		t.Error("the held namespace is no longer terminating")
	}

	// A create in the terminating namespace is refused with a 403 whose cause says why.
	secret := map[string]any{"apiVersion": "v1", "kind": "Secret", "metadata": map[string]any{"name": "vord-it-created"}, "type": "Opaque"}
	createErr := admin.Do(ctx, http.MethodPost, secretsPath, nil, secret, nil)

	var apiError *kube.APIError
	if errors.As(createErr, &apiError) == false {
		t.Fatalf("a create in a terminating namespace gave %v, want an API error", createErr)
	}

	causes := make([]string, 0, len(apiError.Causes))
	for _, cause := range apiError.Causes {
		causes = append(causes, cause.Type)
	}

	if (apiError.Code != http.StatusForbidden) || (apiError.Reason != "Forbidden") || contains(causes, "NamespaceTerminating") == false {
		t.Errorf("a create in a terminating namespace answers %d %q causes %v, want 403 Forbidden with NamespaceTerminating", apiError.Code, apiError.Reason, causes)
	}

	if (kube.IsNamespaceTerminating(createErr) == false) || kube.IsAuthRejected(createErr) {
		t.Errorf("classification of the create: namespace_terminating=%v auth_rejected=%v", kube.IsNamespaceTerminating(createErr), kube.IsAuthRejected(createErr))
	}

	t.Logf("api fact: a POST into a terminating namespace answers HTTP %d reason %q causes %v (%s)", apiError.Code, apiError.Reason, causes, apiError.Message)

	// Enrol meets the terminating namespace and waits for it on a clock of its own, so no real minute passes.
	patient := newFakeClock(time.Now().UTC())
	enrolDir := t.TempDir()
	enrolling := kubereporter.New(kubereporter.Options{DataDir: enrolDir, Facts: s.cluster.factsSource(), Now: patient.Now, Sleep: patient.Sleep})

	enrolCtx, cancel := context.WithTimeout(ctx, commandTimeout)
	defer cancel()

	if enrolled := enrolling.Enrol(enrolCtx); (enrolled.Success) || (enrolled.Message != "namespace_terminating") {
		t.Errorf("enrol into a terminating namespace = %+v, want namespace_terminating", enrolled)
	}

	if _, found, _ := kubereporter.LoadState(enrolDir); found {
		t.Error("enrol into a terminating namespace left a state file")
	}

	// Letting the object go lets the namespace finish.
	held := s.cluster.mustGet(t, configMapsPath+"/vord-it-hold")
	delete(held["metadata"].(map[string]any), "finalizers")

	if _, err := admin.PutRaw(ctx, configMapsPath+"/vord-it-hold", held); err != nil {
		t.Fatalf("release the object that holds the namespace: %v", err)
	}

	eventually(t, namespaceGoneTimeout, "the held namespace to disappear", func() bool {
		_, err := admin.GetRaw(ctx, namespacePath)

		return kube.IsNotFound(err)
	})
}
