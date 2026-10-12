// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package kubereporter

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
	"unicode"

	"github.com/framlux/vord/internal/commands"
	"github.com/framlux/vord/internal/kube"
	"github.com/framlux/vord/internal/kubelocal"
)

// The reasons a command can fail, as they reach the server in Result.Message. A message is the reason alone, or
// the reason, a colon and a short explanation that never holds a credential.
const (
	reasonNoAdminCredential    = "no_admin_credential"
	reasonNotControlPlane      = "not_control_plane"
	reasonAPIUnreachable       = "api_unreachable"
	reasonRBACDenied           = "rbac_denied"
	reasonNameConflict         = "name_conflict"
	reasonNamespaceTerminating = "namespace_terminating"
	reasonTokenNotPopulated    = "token_not_populated"
	reasonCredentialsRejected  = "credentials_rejected"
	reasonTLSCAChanged         = "tls_ca_changed"
	reasonStateWriteFailed     = "state_write_failed"
)

// Success messages. The last two are not failures: unenrol could not delete the Secret and uninstall left
// objects that were not the agent's.
const (
	messageEnrolled                 = "enrolled"
	messageUnenrolled               = "unenrolled"
	messageNotEnrolled              = "not_enrolled"
	messageUninstalled              = "uninstalled"
	messageSecretLeftForUninstall   = "secret_left_for_uninstall"
	messageUninstalledKeptUnmanaged = "uninstalled_unmanaged_objects_kept"
)

const (
	// namespaceWait and namespacePoll bound the wait for a terminating namespace to finish disappearing.
	namespaceWait = 60 * time.Second
	namespacePoll = 2 * time.Second

	// tokenWait and tokenPoll bound the wait for the cluster to fill in a token Secret.
	tokenWait = 30 * time.Second
	tokenPoll = time.Second

	// verifyWait and verifyPoll bound the retries of the final check with the new token. An apiserver behind a
	// load balancer can lag a new token or binding for a moment.
	verifyWait = 15 * time.Second
	verifyPoll = time.Second

	// cleanupTimeout bounds rollback, on a context of its own so a cancelled command still cleans up.
	cleanupTimeout = 30 * time.Second

	// updateAttempts is how often a compare-and-swap update is retried after a Conflict.
	updateAttempts = 5

	// terminatingRounds caps how often a namespace may be waited out and then found terminating again.
	terminatingRounds = 5

	// maxDetailBytes bounds the explanation that follows a reason code.
	maxDetailBytes = 300
)

// LeaseReleaser gives up this host's claim on the reporter Lease and stops the reporter runtime. Unenrol and
// uninstall call it before they delete anything, so the Lease is handed back while the token still works.
type LeaseReleaser interface {
	ReleaseAndStop(ctx context.Context)
}

// NoopLeaseReleaser is the LeaseReleaser for a host that runs no reporter runtime, so has no Lease to hand back.
type NoopLeaseReleaser struct{}

// ReleaseAndStop does nothing.
func (NoopLeaseReleaser) ReleaseAndStop(context.Context) {}

// FactsSource says what this host is: its Kubernetes facts, and whether it is a Kubernetes node at all.
type FactsSource func(ctx context.Context) (kubelocal.Facts, bool)

// Options configures a Reporter.
type Options struct {
	// DataDir is the agent data directory, which holds k8s-reporter.json.
	DataDir string
	// Facts reports what this host is. Nil inspects the real host.
	Facts FactsSource
	// Releaser is called before unenrol and uninstall delete anything. Nil does nothing.
	Releaser LeaseReleaser
	// OnStateChanged runs after every successful enrol, unenrol and uninstall. Nil does nothing.
	OnStateChanged func(ctx context.Context)
	// Now and Sleep are the clock every wait is measured and spent on. Nil means the real ones. Tests inject a
	// clock whose Sleep advances time without waiting.
	Now   func() time.Time
	Sleep func(ctx context.Context, d time.Duration) error
}

// Reporter carries out the three reporter commands. It is safe for concurrent use, though it runs one command at
// a time.
type Reporter struct {
	dataDir        string
	facts          FactsSource
	releaser       LeaseReleaser
	onStateChanged func(ctx context.Context)
	now            func() time.Time
	sleep          func(ctx context.Context, d time.Duration) error

	mu sync.Mutex
}

// A Reporter is what the cluster reporter command executors call.
var _ commands.KubeReporter = (*Reporter)(nil)

// New builds a Reporter.
func New(opts Options) *Reporter {
	r := &Reporter{
		dataDir:        opts.DataDir,
		facts:          opts.Facts,
		releaser:       opts.Releaser,
		onStateChanged: opts.OnStateChanged,
		now:            opts.Now,
		sleep:          opts.Sleep,
	}

	if r.facts == nil {
		r.facts = kubelocal.Detector{Root: "/"}.Detect
	}

	if r.releaser == nil {
		r.releaser = NoopLeaseReleaser{}
	}

	if r.now == nil {
		r.now = time.Now
	}

	if r.sleep == nil {
		r.sleep = sleepContext
	}

	return r
}

// sleepContext waits for d or until ctx ends.
func sleepContext(ctx context.Context, d time.Duration) error {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

// stateChanged tells the hook that the enrolment state changed. The hook gets a context of its own, bounded like
// a cleanup, because a command that used up its deadline has still succeeded and the change is still real.
func (r *Reporter) stateChanged(ctx context.Context) {
	if r.onStateChanged == nil {
		return
	}

	hookCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), cleanupTimeout)
	defer cancel()

	r.onStateChanged(hookCtx)
}

// errPollExpired means a poll used its whole budget without the condition coming true.
var errPollExpired = errors.New("the wait ran out")

// poll runs step now and then every interval until it reports done, fails, the budget is spent or ctx ends.
// The budget is measured on the injected clock: the last step runs once the clock has reached the deadline.
func (r *Reporter) poll(ctx context.Context, interval time.Duration, deadline time.Time, step func(ctx context.Context) (bool, error)) error {
	for {
		done, err := step(ctx)
		if err != nil {
			return err
		}

		if done {
			return nil
		}

		if r.now().Before(deadline) == false {
			return errPollExpired
		}

		if err := r.sleep(ctx, interval); err != nil {
			return err
		}
	}
}

// failure is a command failure: the reason code the server shows, a short explanation that is safe to send, and
// the underlying error for the agent's own log.
type failure struct {
	code   string
	detail string
	err    error
}

// newFailure builds a failure. Only the reasons whose explanation the user needs carry one: what the explanation
// says is built from the agent's own wording, the local kubeconfig problem or the server it tried, never from
// text the cluster sent. Every other reason is sent as its bare code, whatever detail is passed, and the full
// error stays in the agent's log.
func newFailure(code string, detail string, err error) *failure {
	if carriesDetail(code) == false {
		detail = ""
	}

	return &failure{code: code, detail: sanitizeDetail(detail), err: err}
}

// carriesDetail reports whether a failure with this reason code sends an explanation to the server: the server
// that could not be reached, why a credential cannot be used, and which object a name conflict is about.
func carriesDetail(code string) bool {
	switch code {
	case reasonAPIUnreachable, reasonNoAdminCredential, reasonNameConflict:
		return true
	}

	return false
}

// result turns the failure into a command result.
func (f *failure) result() commands.Result {
	message := f.code
	if f.detail != "" {
		message += ": " + f.detail
	}

	cause := errors.New(message)
	if f.err != nil {
		cause = fmt.Errorf("%s: %w", message, f.err)
	}

	return commands.Result{Success: false, Message: message, Error: cause}
}

// sanitizeDetail removes control characters and bounds the length of an explanation.
func sanitizeDetail(detail string) string {
	cleaned := strings.Map(func(r rune) rune {
		if unicode.IsPrint(r) == false {
			return ' '
		}

		return r
	}, detail)

	cleaned = strings.TrimSpace(cleaned)
	if len(cleaned) > maxDetailBytes {
		cleaned = strings.ToValidUTF8(cleaned[:maxDetailBytes], "") + "..."
	}

	return cleaned
}

// conflictError means an object has the name of one of ours but not the Vord label.
type conflictError struct {
	kind string
	name string
}

func (e *conflictError) Error() string {
	return fmt.Sprintf("%s %s exists without the %s=%s label", e.kind, e.name, managedByLabel, managedByValue)
}

// errNamespaceTerminating means the namespace is being deleted.
var errNamespaceTerminating = errors.New("the vord-system namespace is being deleted")

// adminSession is a client for the cluster with the admin credential, and where it points. The credential
// itself lives only inside the client.
type adminSession struct {
	client        *kube.Client
	server        string // for messages, without any user information
	serverURL     string // as the kubeconfig names it
	tlsServerName string
}

// openAdmin checks the host may act on the cluster and builds a client from its admin kubeconfig. The
// credential stays inside the returned client and is never logged or sent.
func (r *Reporter) openAdmin(ctx context.Context) (*adminSession, *failure) {
	facts, isNode := r.facts(ctx)
	if (isNode == false) || (facts.ControlPlane == false) {
		return nil, newFailure(reasonNotControlPlane, "", nil)
	}

	return r.adminFromFacts(facts)
}

// adminFromFacts builds the admin client for a host whose facts say it has the credential.
func (r *Reporter) adminFromFacts(facts kubelocal.Facts) (*adminSession, *failure) {
	if (facts.AdminCredentialPresent == false) || (facts.AdminKubeconfigPath == "") {
		return nil, newFailure(reasonNoAdminCredential, "the admin kubeconfig was not found on this host", nil)
	}

	config, err := kube.LoadKubeconfig(facts.AdminKubeconfigPath, nil)
	if err != nil {
		return nil, newFailure(reasonNoAdminCredential, err.Error(), err)
	}

	client, err := kube.New(config)
	if err != nil {
		return nil, newFailure(reasonNoAdminCredential, err.Error(), err)
	}

	return &adminSession{
		client:        client,
		server:        redactedServer(config.Server),
		serverURL:     config.Server,
		tlsServerName: config.TLSServerName,
	}, nil
}

// classify turns an error from an admin call into the failure it deserves.
func (s *adminSession) classify(err error) *failure {
	var conflict *conflictError

	switch {
	case errors.As(err, &conflict):
		return newFailure(reasonNameConflict, conflict.Error(), err)
	case errors.Is(err, errNamespaceTerminating) || kube.IsNamespaceTerminating(err):
		return newFailure(reasonNamespaceTerminating, "", err)
	case kube.IsAuthRejected(err):
		return newFailure(reasonRBACDenied, "", err)
	}

	return newFailure(reasonAPIUnreachable, unreachableDetail(s.server, err), err)
}

// unreachableDetail explains a failed call to the cluster: the server that was tried and a coarse cause worked
// out here. It never repeats what the cluster said.
func unreachableDetail(server string, err error) string {
	return fmt.Sprintf("cannot reach the API server at %s (%s)", server, describeUnreachable(err))
}

// describeUnreachable names the cause of a failed call in words of our own.
func describeUnreachable(err error) string {
	var apiError *kube.APIError

	var netError net.Error

	switch {
	case errors.As(err, &apiError):
		return fmt.Sprintf("it answered with status %d", apiError.Code)
	case kube.IsTLSVerify(err):
		return "its certificate could not be verified"
	case errors.Is(err, context.Canceled):
		return "the command was cancelled"
	case errors.Is(err, context.DeadlineExceeded) || (errors.As(err, &netError) && netError.Timeout()):
		return "the request timed out"
	}

	return "the connection failed"
}

// redactedServer returns server with any user information removed.
func redactedServer(server string) string {
	parsed, err := url.Parse(server)
	if err != nil {
		return "the API server"
	}

	return parsed.Redacted()
}

// newInstanceID makes the 16 hex characters that identify one candidate.
func newInstanceID() (string, error) {
	buffer := make([]byte, 8)
	if _, err := rand.Read(buffer); err != nil {
		return "", fmt.Errorf("generate instance id: %w", err)
	}

	return hex.EncodeToString(buffer), nil
}

// stringAt reads a string from nested generic JSON objects, and is "" when any step is missing.
func stringAt(obj map[string]any, path ...string) string {
	var current any = obj

	for _, key := range path {
		next, ok := current.(map[string]any)
		if ok == false {
			return ""
		}

		current = next[key]
	}

	text, _ := current.(string)

	return text
}

// isManaged reports whether obj carries the Vord label.
func isManaged(obj map[string]any) bool {
	return stringAt(obj, "metadata", "labels", managedByLabel) == managedByValue
}

// isTerminating reports whether a namespace object is being deleted.
func isTerminating(obj map[string]any) bool {
	return (stringAt(obj, "metadata", "deletionTimestamp") != "") || (stringAt(obj, "status", "phase") == "Terminating")
}

// deleteOptions is the body of a DELETE that only deletes the object with the given uid, so an object replaced
// since it was checked is left alone.
func deleteOptions(uid string) any {
	if uid == "" {
		return nil
	}

	return map[string]any{
		"apiVersion":    "v1",
		"kind":          "DeleteOptions",
		"preconditions": map[string]any{"uid": uid},
	}
}

// deleteOutcome says what deleteManaged found.
type deleteOutcome int

const (
	// outcomeAbsent means there was nothing to delete.
	outcomeAbsent deleteOutcome = iota
	// outcomeDeleted means the object was ours and is gone.
	outcomeDeleted
	// outcomeUnmanaged means the object exists without the Vord label and was left alone.
	outcomeUnmanaged
)

// deleteManaged deletes the object at path when it carries the Vord label, and only that exact object: the
// delete names the uid it checked. An object that is not there counts as deleted; one without the label is left
// alone. A delete refused because the object was replaced starts again from the read.
func deleteManaged(ctx context.Context, client *kube.Client, path string) (deleteOutcome, error) {
	var last error

	for attempt := 0; attempt < updateAttempts; attempt++ {
		obj, err := client.GetRaw(ctx, path)
		if err != nil {
			if kube.IsNotFound(err) {
				return outcomeAbsent, nil
			}

			return outcomeAbsent, err
		}

		if isManaged(obj) == false {
			return outcomeUnmanaged, nil
		}

		err = client.Do(ctx, http.MethodDelete, path, nil, deleteOptions(stringAt(obj, "metadata", "uid")), nil)

		switch {
		case err == nil:
			return outcomeDeleted, nil
		case kube.IsNotFound(err):
			return outcomeAbsent, nil
		case kube.IsConflict(err):
			last = err

			continue
		}

		return outcomeAbsent, err
	}

	return outcomeAbsent, last
}
