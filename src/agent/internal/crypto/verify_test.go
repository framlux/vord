// Copyright (c) 2026 Framlux LLC
// Licensed under the MIT License
// See LICENSE for details.

package crypto

import (
	"crypto/ed25519"
	"crypto/rand"
	"strings"
	"testing"
	"time"
)

func TestBuildCanonicalPayload_Deterministic(t *testing.T) {
	p := CanonicalPayload{
		CommandID:   "test-uuid",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   42,
		Nonce:       "abc123",
		Params:      map[string]string{"key": "value"},
		TenantID:    7,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      3,
	}

	data1, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	data2, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	if string(data1) != string(data2) {
		t.Errorf("non-deterministic output:\n%s\nvs\n%s", data1, data2)
	}
}

func TestVerifySignature_RoundTrip(t *testing.T) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	payload := CanonicalPayload{
		CommandID:   "test-uuid",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   42,
		Nonce:       "abc123",
		Params:      map[string]string{},
		TenantID:    7,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      3,
	}

	data, err := BuildCanonicalPayload(payload)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	sig := ed25519.Sign(priv, data)

	if VerifySignature(pub, data, sig) == false {
		t.Error("valid signature rejected")
	}
}

func TestVerifySignature_InvalidSignature(t *testing.T) {
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	data := []byte(`{"test":"data"}`)
	badSig := make([]byte, ed25519.SignatureSize)

	if VerifySignature(pub, data, badSig) {
		t.Error("invalid signature accepted")
	}
}

func TestVerifySignature_WrongKey(t *testing.T) {
	_, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	otherPub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}

	data := []byte(`{"test":"data"}`)
	sig := ed25519.Sign(priv, data)

	if VerifySignature(otherPub, data, sig) {
		t.Error("wrong key accepted")
	}
}

// testNow is the fixed instant every timestamp test measures against.
var testNow = time.Date(2026, time.October, 11, 12, 0, 0, 0, time.UTC)

func TestValidateTimestamps_Valid(t *testing.T) {
	ts := testNow.Format(time.RFC3339)
	exp := testNow.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, exp, 5*time.Minute)
	if err != nil {
		t.Errorf("expected valid, got: %v", err)
	}
}

func TestValidateTimestamps_Expired(t *testing.T) {
	ts := testNow.Add(-10 * time.Minute).Format(time.RFC3339)
	exp := testNow.Add(-5 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, exp, 15*time.Minute)
	if err == nil {
		t.Error("expected expired error")
	}
}

func TestValidateTimestamps_TooOld(t *testing.T) {
	ts := testNow.Add(-10 * time.Minute).Format(time.RFC3339)
	exp := testNow.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, exp, 5*time.Minute)
	if err == nil {
		t.Error("expected too-old error")
	}
}

// Intent: Timestamp far in the future is rejected to guard against clock skew attacks.
func TestValidateTimestamps_FutureTimestamp(t *testing.T) {
	// Timestamp 20 minutes in the future with maxAge=5min should fail.
	ts := testNow.Add(20 * time.Minute).Format(time.RFC3339)
	exp := testNow.Add(30 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, exp, 5*time.Minute)
	if err == nil {
		t.Error("expected future-timestamp error")
	}
}

// Intent: Timestamp slightly in the future (within maxAge) is accepted for clock skew tolerance.
func TestValidateTimestamps_SlightlyFutureAccepted(t *testing.T) {
	// Timestamp 2 minutes in the future with maxAge=5min should be OK.
	ts := testNow.Add(2 * time.Minute).Format(time.RFC3339)
	exp := testNow.Add(10 * time.Minute).Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, exp, 5*time.Minute)
	if err != nil {
		t.Errorf("expected slightly-future timestamp to be accepted, got: %v", err)
	}
}

// Intent: Invalid timestamp format returns error.
func TestValidateTimestamps_InvalidFormat(t *testing.T) {
	err := ValidateTimestamps(testNow, "not-a-date", "also-not-a-date", 5*time.Minute)
	if err == nil {
		t.Error("expected error for invalid timestamp format")
	}
}

// Intent: Valid timestamp but invalid expires_at format returns error.
func TestValidateTimestamps_InvalidExpiresFormat(t *testing.T) {
	ts := testNow.Format(time.RFC3339)

	err := ValidateTimestamps(testNow, ts, "not-a-date", 5*time.Minute)
	if err == nil {
		t.Error("expected error for invalid expires_at format")
	}
}

// Intent: The timestamp forms the web client and the server emit are both accepted, and are measured
// against the injected instant rather than the wall clock.
func TestValidateTimestamps_AcceptsFractionalSecondsAndOffsets(t *testing.T) {
	cases := []struct {
		name string
		ts   string
		exp  string
	}{
		{"javascript toISOString", "2026-10-11T12:00:00.000Z", "2026-10-11T12:10:00.000Z"},
		{"dotnet round-trip format", "2026-10-11T12:00:00.0000000+00:00", "2026-10-11T12:10:00.0000000+00:00"},
		{"non-utc offset", "2026-10-11T14:00:00.000+02:00", "2026-10-11T14:10:00.000+02:00"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if err := ValidateTimestamps(testNow, tc.ts, tc.exp, 5*time.Minute); err != nil {
				t.Errorf("expected valid, got: %v", err)
			}
		})
	}
}

// Intent: Expiry is judged at the injected instant, so a command is valid just before expires_at and
// rejected just after it.
func TestValidateTimestamps_ExpiryBoundaryUsesInjectedClock(t *testing.T) {
	ts := "2026-10-11T12:00:00Z"
	exp := "2026-10-11T12:10:00Z"

	if err := ValidateTimestamps(testNow.Add(10*time.Minute), ts, exp, 15*time.Minute); err != nil {
		t.Errorf("expected valid at the expiry instant, got: %v", err)
	}
	if err := ValidateTimestamps(testNow.Add(10*time.Minute+time.Second), ts, exp, 15*time.Minute); err == nil {
		t.Error("expected expired one second after expires_at")
	}
}

// Intent: VerifySignature rejects truncated keys and signatures.
func TestVerifySignature_InvalidKeyAndSigSizes(t *testing.T) {
	if VerifySignature([]byte("short"), []byte("data"), make([]byte, ed25519.SignatureSize)) {
		t.Error("expected false for short public key")
	}

	pub, _, _ := ed25519.GenerateKey(rand.Reader)
	if VerifySignature(pub, []byte("data"), []byte("short-sig")) {
		t.Error("expected false for short signature")
	}
}

// Intent: BuildCanonicalPayload with nil Params uses empty map (not null in JSON).
func TestBuildCanonicalPayload_NilParams(t *testing.T) {
	p := CanonicalPayload{
		CommandID:   "test",
		CommandType: "reboot",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   1,
		Nonce:       "abc",
		Params:      nil,
		TenantID:    1,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      1,
	}

	data, err := BuildCanonicalPayload(p)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	// Params should be serialized as {} not null.
	if strings.Contains(string(data), `"params":{}`) == false {
		t.Errorf("expected nil params serialized as an empty object, got %s", data)
	}
}

// webSignedPayload is the exact byte string the web client signs for a command with no parameters:
// sorted keys, no whitespace, no params key, and Date.toISOString() timestamps.
const webSignedPayload = `{"command_id":"11111111-2222-3333-4444-555555555555","command_type":"check_updates","expires_at":"2026-10-11T12:10:00.000Z","machine_id":200,"nonce":"0123456789abcdef0123456789abcdef","tenant_id":100,"timestamp":"2026-10-11T12:00:00.000Z","user_id":42}`

// Intent: The payload the web client actually signs, which carries no params key, parses and its absent
// params decode as an empty (non-nil) map.
func TestParseCanonicalPayload_WebPayloadWithoutParams(t *testing.T) {
	got, err := ParseCanonicalPayload([]byte(webSignedPayload))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}

	want := CanonicalPayload{
		CommandID:   "11111111-2222-3333-4444-555555555555",
		CommandType: "check_updates",
		ExpiresAt:   "2026-10-11T12:10:00.000Z",
		MachineID:   200,
		Nonce:       "0123456789abcdef0123456789abcdef",
		TenantID:    100,
		Timestamp:   "2026-10-11T12:00:00.000Z",
		UserID:      42,
	}
	if got.CommandID != want.CommandID || got.CommandType != want.CommandType || got.ExpiresAt != want.ExpiresAt ||
		got.MachineID != want.MachineID || got.Nonce != want.Nonce || got.TenantID != want.TenantID ||
		got.Timestamp != want.Timestamp || got.UserID != want.UserID {
		t.Errorf("decoded %+v, want %+v", got, want)
	}
	if got.Params == nil {
		t.Error("expected absent params to decode as a non-nil empty map")
	}
	if len(got.Params) != 0 {
		t.Errorf("expected empty params, got %v", got.Params)
	}
}

// Intent: Params that are present decode to exactly the signed key/value pairs.
func TestParseCanonicalPayload_WithParams(t *testing.T) {
	payload := strings.Replace(webSignedPayload, `"nonce"`, `"params":{"pid":"1234","signal":"TERM"},"nonce"`, 1)

	got, err := ParseCanonicalPayload([]byte(payload))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}
	if len(got.Params) != 2 || got.Params["pid"] != "1234" || got.Params["signal"] != "TERM" {
		t.Errorf("unexpected params: %v", got.Params)
	}
}

// Intent: A payload built by BuildCanonicalPayload, which always writes params, parses back to the same fields.
func TestParseCanonicalPayload_RoundTripsBuildCanonicalPayload(t *testing.T) {
	in := CanonicalPayload{
		CommandID:   "id-1",
		CommandType: "kill_process",
		ExpiresAt:   "2026-03-12T15:10:00Z",
		MachineID:   42,
		Nonce:       "abc123",
		Params:      map[string]string{"pid": "7"},
		TenantID:    7,
		Timestamp:   "2026-03-12T15:00:00Z",
		UserID:      3,
	}

	data, err := BuildCanonicalPayload(in)
	if err != nil {
		t.Fatalf("BuildCanonicalPayload: %v", err)
	}

	out, err := ParseCanonicalPayload(data)
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}
	if out.CommandID != in.CommandID || out.CommandType != in.CommandType || out.MachineID != in.MachineID ||
		out.Nonce != in.Nonce || out.TenantID != in.TenantID || out.UserID != in.UserID ||
		out.Timestamp != in.Timestamp || out.ExpiresAt != in.ExpiresAt || out.Params["pid"] != "7" {
		t.Errorf("round trip changed the payload: %+v vs %+v", out, in)
	}
}

// Intent: Anything that could make two readers disagree about what was signed is a parse error: unknown
// keys, duplicates (which encoding/json resolves by letting the last win), keys that differ only in case
// (which encoding/json matches to the known field), and values of the wrong shape.
func TestParseCanonicalPayload_RejectsAmbiguousOrMalformedPayloads(t *testing.T) {
	cases := []struct {
		name    string
		payload string
		wantErr string
	}{
		{
			name:    "unknown key",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"extra":"x","nonce"`, 1),
			wantErr: "unknown key",
		},
		{
			name:    "duplicate command_type",
			payload: strings.Replace(webSignedPayload, `"command_type":"check_updates"`, `"command_type":"reboot","command_type":"check_updates"`, 1),
			wantErr: "duplicate key",
		},
		{
			name:    "duplicate machine_id",
			payload: strings.Replace(webSignedPayload, `"machine_id":200`, `"machine_id":999,"machine_id":200`, 1),
			wantErr: "duplicate key",
		},
		{
			name:    "duplicate params",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":{},"params":{},"nonce"`, 1),
			wantErr: "duplicate key",
		},
		{
			name:    "case variant alongside the real key",
			payload: strings.Replace(webSignedPayload, `"command_type":"check_updates"`, `"command_type":"check_updates","COMMAND_TYPE":"reboot"`, 1),
			wantErr: "differs only in case",
		},
		{
			name:    "case variant instead of the real key",
			payload: strings.Replace(webSignedPayload, `"command_type"`, `"COMMAND_TYPE"`, 1),
			wantErr: "differs only in case",
		},
		{
			name:    "mixed case params",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"Params":{"a":"b"},"nonce"`, 1),
			wantErr: "differs only in case",
		},
		{
			name:    "duplicate key inside params",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":{"a":"b","a":"c"},"nonce"`, 1),
			wantErr: "duplicate",
		},
		{
			name:    "params value is a number",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":{"a":1},"nonce"`, 1),
			wantErr: "params",
		},
		{
			name:    "params value is null",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":{"a":null},"nonce"`, 1),
			wantErr: "params",
		},
		{
			name:    "params value is nested",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":{"a":{"b":"c"}},"nonce"`, 1),
			wantErr: "params",
		},
		{
			name:    "params is null",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":null,"nonce"`, 1),
			wantErr: "params",
		},
		{
			name:    "params is an array",
			payload: strings.Replace(webSignedPayload, `"nonce"`, `"params":[],"nonce"`, 1),
			wantErr: "params",
		},
		{
			name:    "string field is null",
			payload: strings.Replace(webSignedPayload, `"command_type":"check_updates"`, `"command_type":null`, 1),
			wantErr: "command_type",
		},
		{
			name:    "string field is a number",
			payload: strings.Replace(webSignedPayload, `"nonce":"0123456789abcdef0123456789abcdef"`, `"nonce":12`, 1),
			wantErr: "nonce",
		},
		{
			name:    "machine_id is a string",
			payload: strings.Replace(webSignedPayload, `"machine_id":200`, `"machine_id":"200"`, 1),
			wantErr: "machine_id",
		},
		{
			name:    "machine_id is fractional",
			payload: strings.Replace(webSignedPayload, `"machine_id":200`, `"machine_id":200.5`, 1),
			wantErr: "machine_id",
		},
		{
			name:    "machine_id in exponent form",
			payload: strings.Replace(webSignedPayload, `"machine_id":200`, `"machine_id":2e2`, 1),
			wantErr: "machine_id",
		},
		{
			name:    "tenant_id overflows int32",
			payload: strings.Replace(webSignedPayload, `"tenant_id":100`, `"tenant_id":2147483648`, 1),
			wantErr: "tenant_id",
		},
		{
			name:    "user_id is null",
			payload: strings.Replace(webSignedPayload, `"user_id":42`, `"user_id":null`, 1),
			wantErr: "user_id",
		},
		{
			name:    "missing command_id",
			payload: strings.Replace(webSignedPayload, `"command_id":"11111111-2222-3333-4444-555555555555",`, ``, 1),
			wantErr: "missing",
		},
		{
			name:    "missing nonce",
			payload: strings.Replace(webSignedPayload, `"nonce":"0123456789abcdef0123456789abcdef",`, ``, 1),
			wantErr: "missing",
		},
		{
			name:    "missing user_id",
			payload: strings.Replace(webSignedPayload, `,"user_id":42`, ``, 1),
			wantErr: "missing",
		},
		{
			name:    "trailing data after the object",
			payload: webSignedPayload + `{}`,
			wantErr: "trailing",
		},
		{
			name:    "trailing garbage after the object",
			payload: webSignedPayload + ` x`,
			wantErr: "",
		},
		{
			name:    "trailing comma",
			payload: strings.Replace(webSignedPayload, `"user_id":42}`, `"user_id":42,}`, 1),
			wantErr: "",
		},
		{
			name:    "missing colon",
			payload: strings.Replace(webSignedPayload, `"nonce":`, `"nonce" `, 1),
			wantErr: "",
		},
		{
			name:    "truncated object",
			payload: webSignedPayload[:len(webSignedPayload)-1],
			wantErr: "",
		},
		{
			name:    "not an object",
			payload: `[]`,
			wantErr: "object",
		},
		{
			name:    "empty input",
			payload: ``,
			wantErr: "",
		},
		{
			name:    "empty object",
			payload: `{}`,
			wantErr: "missing",
		},
		{
			name:    "invalid UTF-8",
			payload: strings.Replace(webSignedPayload, `"check_updates"`, "\"check_\xff\xfeupdates\"", 1),
			wantErr: "UTF-8",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := ParseCanonicalPayload([]byte(tc.payload))
			if err == nil {
				t.Fatalf("expected an error for %s", tc.payload)
			}
			if tc.wantErr != "" && strings.Contains(err.Error(), tc.wantErr) == false {
				t.Errorf("expected error containing %q, got %q", tc.wantErr, err.Error())
			}
		})
	}
}

// Intent: Whitespace between tokens does not change what was signed, so a payload with whitespace parses.
func TestParseCanonicalPayload_AcceptsWhitespaceBetweenTokens(t *testing.T) {
	spaced := strings.ReplaceAll(strings.ReplaceAll(webSignedPayload, `,"`, ", \n\t\""), `":`, `" : `)

	got, err := ParseCanonicalPayload([]byte(spaced))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}
	if got.CommandType != "check_updates" || got.MachineID != 200 {
		t.Errorf("unexpected decode: %+v", got)
	}
}

// Intent: Escaped characters in string values decode the way JSON defines them.
func TestParseCanonicalPayload_DecodesEscapes(t *testing.T) {
	payload := strings.Replace(webSignedPayload, `"nonce"`, `"params":{"path":"a\\b\"c\u00e9"},"nonce"`, 1)

	got, err := ParseCanonicalPayload([]byte(payload))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}
	if got.Params["path"] != "a\\b\"c\u00e9" {
		t.Errorf("unexpected decoded param: %q", got.Params["path"])
	}
}

// matchingCommand returns a delivered command that BindsTo accepts for the web-signed payload as the server
// re-emits it: .NET round-trip timestamps and nil params.
func matchingCommand() BoundCommand {
	return BoundCommand{
		CommandID:   "11111111-2222-3333-4444-555555555555",
		CommandType: "check_updates",
		ExpiresAt:   "2026-10-11T12:10:00.0000000+00:00",
		MachineID:   200,
		Nonce:       "0123456789abcdef0123456789abcdef",
		Params:      nil,
		TenantID:    100,
		Timestamp:   "2026-10-11T12:00:00.0000000+00:00",
		UserID:      42,
	}
}

func parseWebPayload(t *testing.T) CanonicalPayload {
	t.Helper()

	p, err := ParseCanonicalPayload([]byte(webSignedPayload))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}

	return p
}

// Intent: The web-signed payload binds to the command the server delivers even though the timestamp text
// differs (".000Z" versus ".0000000+00:00") and the delivered params are nil rather than absent.
func TestBindsTo_WebPayloadAndServerRoundTripBind(t *testing.T) {
	if parseWebPayload(t).BindsTo(matchingCommand()) == false {
		t.Error("expected the signed payload to bind to the delivered command")
	}
}

// Intent: Timestamps are compared as instants, so equivalent renderings bind and different instants do not.
func TestBindsTo_TimestampsCompareAsInstants(t *testing.T) {
	p := parseWebPayload(t)

	// The signed timestamp is 12:00:00Z and the signed expires_at is 12:10:00Z.
	cases := []struct {
		name      string
		timestamp string
		expiresAt string
		binds     bool
	}{
		{"same instant, fraction omitted", "2026-10-11T12:00:00Z", "2026-10-11T12:10:00Z", true},
		{"same instant, millisecond fraction", "2026-10-11T12:00:00.000Z", "2026-10-11T12:10:00.000Z", true},
		{"same instant, dotnet round-trip format", "2026-10-11T12:00:00.0000000+00:00", "2026-10-11T12:10:00.0000000+00:00", true},
		{"same instant, positive offset", "2026-10-11T14:00:00+02:00", "2026-10-11T14:10:00+02:00", true},
		{"same instant, negative offset", "2026-10-11T07:00:00.0000000-05:00", "2026-10-11T07:10:00.0000000-05:00", true},
		{"timestamp one second later", "2026-10-11T12:00:01Z", "2026-10-11T12:10:00Z", false},
		{"timestamp one millisecond later", "2026-10-11T12:00:00.001Z", "2026-10-11T12:10:00Z", false},
		{"timestamp one tick later", "2026-10-11T12:00:00.0000001+00:00", "2026-10-11T12:10:00Z", false},
		{"timestamp same wall clock in another offset", "2026-10-11T12:00:00+02:00", "2026-10-11T12:10:00Z", false},
		{"expires_at one second later", "2026-10-11T12:00:00Z", "2026-10-11T12:10:01Z", false},
		{"expires_at same wall clock in another offset", "2026-10-11T12:00:00Z", "2026-10-11T12:10:00+02:00", false},
		{"timestamp without an offset", "2026-10-11T12:00:00", "2026-10-11T12:10:00Z", false},
		{"timestamp unparseable", "not-a-timestamp", "2026-10-11T12:10:00Z", false},
		{"timestamp empty", "", "2026-10-11T12:10:00Z", false},
		{"expires_at unparseable", "2026-10-11T12:00:00Z", "not-a-timestamp", false},
		{"expires_at empty", "2026-10-11T12:00:00Z", "", false},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cmd := matchingCommand()
			cmd.Timestamp = tc.timestamp
			cmd.ExpiresAt = tc.expiresAt
			if p.BindsTo(cmd) != tc.binds {
				t.Errorf("BindsTo(timestamp=%q, expires_at=%q) = %v, want %v", tc.timestamp, tc.expiresAt, p.BindsTo(cmd), tc.binds)
			}
		})
	}
}

// Intent: Every signed field must match the delivered command; a difference in any one of them is a mismatch.
func TestBindsTo_EachFieldMustMatch(t *testing.T) {
	p := parseWebPayload(t)

	mutations := map[string]func(*BoundCommand){
		"command id":   func(c *BoundCommand) { c.CommandID = "other" },
		"command type": func(c *BoundCommand) { c.CommandType = "reboot" },
		"nonce":        func(c *BoundCommand) { c.Nonce = "other" },
		"machine":      func(c *BoundCommand) { c.MachineID = 201 },
		"tenant":       func(c *BoundCommand) { c.TenantID = 101 },
		"user":         func(c *BoundCommand) { c.UserID = 43 },
		"expires_at":   func(c *BoundCommand) { c.ExpiresAt = "2026-10-11T12:10:01.0000000+00:00" },
		"timestamp":    func(c *BoundCommand) { c.Timestamp = "2026-10-11T12:00:01.0000000+00:00" },
		"params":       func(c *BoundCommand) { c.Params = map[string]string{"a": "b"} },
	}

	for name, mutate := range mutations {
		t.Run(name, func(t *testing.T) {
			cmd := matchingCommand()
			mutate(&cmd)
			if p.BindsTo(cmd) {
				t.Errorf("a differing %s must not bind", name)
			}
		})
	}
}

// Intent: An absent params key and an empty delivered map are the same, as are an empty signed object and
// nil, but any real difference in content is a mismatch.
func TestBindsTo_ParamsNilEqualsEmptyAndContentMustMatch(t *testing.T) {
	absent := parseWebPayload(t)

	cmd := matchingCommand()
	cmd.Params = map[string]string{}
	if absent.BindsTo(cmd) == false {
		t.Error("absent signed params must bind to an empty delivered map")
	}

	emptySigned := strings.Replace(webSignedPayload, `"nonce"`, `"params":{},"nonce"`, 1)
	p, err := ParseCanonicalPayload([]byte(emptySigned))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}
	cmd.Params = nil
	if p.BindsTo(cmd) == false {
		t.Error("empty signed params must bind to nil delivered params")
	}

	withParams := strings.Replace(webSignedPayload, `"nonce"`, `"params":{"a":"b"},"nonce"`, 1)
	signed, err := ParseCanonicalPayload([]byte(withParams))
	if err != nil {
		t.Fatalf("ParseCanonicalPayload: %v", err)
	}

	cases := []struct {
		name   string
		params map[string]string
		binds  bool
	}{
		{"same content", map[string]string{"a": "b"}, true},
		{"nil against signed content", nil, false},
		{"empty against signed content", map[string]string{}, false},
		{"different value", map[string]string{"a": "c"}, false},
		{"different key", map[string]string{"A": "b"}, false},
		{"extra key", map[string]string{"a": "b", "c": "d"}, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := matchingCommand()
			c.Params = tc.params
			if signed.BindsTo(c) != tc.binds {
				t.Errorf("BindsTo = %v, want %v", signed.BindsTo(c), tc.binds)
			}
		})
	}

	cmd.Params = map[string]string{"a": "b"}
	if absent.BindsTo(cmd) {
		t.Error("delivered params that were never signed must not bind")
	}
}

// Intent: A payload that was never parsed (zero value) binds to nothing it should not, and an unparseable
// signed timestamp never binds.
func TestBindsTo_UnparseableSignedTimestampNeverBinds(t *testing.T) {
	p := parseWebPayload(t)
	p.Timestamp = "garbage"

	if p.BindsTo(matchingCommand()) {
		t.Error("an unparseable signed timestamp must not bind")
	}

	p = parseWebPayload(t)
	p.ExpiresAt = "garbage"
	if p.BindsTo(matchingCommand()) {
		t.Error("an unparseable signed expires_at must not bind")
	}
}
