#!/usr/bin/env bash
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Minimal manual smoke test for install.sh's flag parsing and non-interactive resolution. Run
# directly:
#   bash deployment/agent/install_test.sh
# Not wired into CI — invoke by hand when changing the flag contract.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_SH="${SCRIPT_DIR}/install.sh"
FAILURES=0

pass() {
    printf "  PASS: %s\n" "$1"
}

fail() {
    printf "  FAIL: %s\n" "$1"
    FAILURES=$((FAILURES + 1))
}

# Runs "$@" in the background and kills it if it hasn't finished within TIMEOUT_SECS, so a test
# that is supposed to prove "does not block" can't itself hang the suite forever if the behavior
# it is checking for regresses. macOS ships no `timeout`/`gtimeout` binary by default, hence the
# manual poll loop instead of `timeout "$@"`.
TIMEOUT_SECS=5

run_with_timeout() {
    "$@" &
    local child_pid=$!
    local waited=0
    local max_waits=$((TIMEOUT_SECS * 5))

    while kill -0 "${child_pid}" 2>/dev/null; do
        sleep 0.2
        waited=$((waited + 1))
        if [ "${waited}" -ge "${max_waits}" ]; then
            kill -9 "${child_pid}" 2>/dev/null || true
            wait "${child_pid}" 2>/dev/null || true
            return 124
        fi
    done

    wait "${child_pid}"
}

echo "== bash -n syntax check =="
if bash -n "${INSTALL_SH}"; then
    pass "syntax is valid"
else
    fail "syntax check failed"
fi

echo "== --help exits 0 and prints usage without requiring root =="
# Capturing the exit status of a failing command substitution directly (STATUS=$?) is dead code
# under `set -e`: the assignment itself would abort the script before the next line ever ran.
# `||` on the assignment is what actually lets us observe a non-zero exit here.
HELP_STATUS=0
HELP_OUTPUT="$(bash "${INSTALL_SH}" --help)" || HELP_STATUS=$?
if [ "${HELP_STATUS}" -eq 0 ]; then
    pass "--help exits 0"
else
    fail "--help exited ${HELP_STATUS}"
fi
if echo "${HELP_OUTPUT}" | grep -q -- "--token"; then
    pass "--help documents --token"
else
    fail "--help missing --token"
fi
if echo "${HELP_OUTPUT}" | grep -q -- "--server"; then
    pass "--help documents --server"
else
    fail "--help missing --server"
fi
if echo "${HELP_OUTPUT}" | grep -q -- "--update"; then
    pass "--help documents --update"
else
    fail "--help missing --update"
fi
if echo "${HELP_OUTPUT}" | grep -q "VORD_REGISTRATION_TOKEN"; then
    pass "--help documents the env-var form"
else
    fail "--help missing env-var form"
fi

echo "== the documented piped form actually runs main =="
# Regression guard for the defect that made `curl -fsSL https://get.vordfleet.dev | sudo bash`
# a no-op: the old `[ "${BASH_SOURCE[0]}" = "${0}" ]` entry guard aborted under `set -u` with
# "BASH_SOURCE[0]: unbound variable" when the script arrived on stdin, so the installer never
# started. --help is used because it is the only path that reaches main without needing root.
PIPED_STATUS=0
PIPED_OUTPUT="$(bash -c 'cat "$1" | bash -s -- --help' _ "${INSTALL_SH}" 2>&1)" || PIPED_STATUS=$?
if [ "${PIPED_STATUS}" -ne 0 ]; then
    fail "piped invocation exited ${PIPED_STATUS}: ${PIPED_OUTPUT}"
else
    pass "piped invocation exits 0"
fi
if echo "${PIPED_OUTPUT}" | grep -q "Vord Agent installer"; then
    pass "piped invocation reached main and printed usage"
else
    fail "piped invocation produced no usage output: ${PIPED_OUTPUT}"
fi
if echo "${PIPED_OUTPUT}" | grep -q "unbound variable"; then
    fail "piped invocation hit an unbound variable"
else
    pass "piped invocation has no unbound-variable error"
fi

echo "== sourcing still does NOT run main =="
SOURCED_OUTPUT="$(bash -c 'set -euo pipefail; source "$1"; echo SOURCED_OK' _ "${INSTALL_SH}" 2>&1)" || true
if echo "${SOURCED_OUTPUT}" | grep -q "SOURCED_OK"; then
    pass "sourcing loads the functions without running main"
else
    fail "sourcing ran main or failed: ${SOURCED_OUTPUT}"
fi

echo "== unknown flag exits non-zero =="
if bash "${INSTALL_SH}" --bogus >/dev/null 2>&1; then
    fail "unknown flag should have failed"
else
    pass "unknown flag exits non-zero"
fi

echo "== --token without a value exits non-zero =="
if bash "${INSTALL_SH}" --token >/dev/null 2>&1; then
    fail "--token with no value should have failed"
else
    pass "--token with no value exits non-zero"
fi

echo "== --server without a value exits non-zero =="
if bash "${INSTALL_SH}" --server >/dev/null 2>&1; then
    fail "--server with no value should have failed"
else
    pass "--server with no value exits non-zero"
fi

# The functions below are sourced directly (install.sh only runs `main` when executed, not when
# sourced — see the BASH_SOURCE guard at the bottom of the file) so this exercises the exact
# non-interactive resolution logic without root or a package manager.

echo "== --token alone (no --server, no VORD_SERVER_ADDRESS) resolves the default server without blocking =="
RESOLVE_STATUS=0
RESOLVE_OUTPUT="$(
    run_with_timeout bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        VORD_REGISTRATION_TOKEN="test-token"
        resolve_noninteractive
        resolve_server_address
        printf "NONINTERACTIVE=%s SERVER_ADDRESS=%s" "${NONINTERACTIVE}" "${SERVER_ADDRESS}"
    '
)" || RESOLVE_STATUS=$?

if [ "${RESOLVE_STATUS}" -eq 124 ]; then
    fail "blocked for ${TIMEOUT_SECS}s+ instead of resolving the default server"
elif [ "${RESOLVE_STATUS}" -ne 0 ]; then
    fail "resolution exited ${RESOLVE_STATUS}: ${RESOLVE_OUTPUT}"
else
    pass "did not block"
fi
if echo "${RESOLVE_OUTPUT}" | grep -q "NONINTERACTIVE=1"; then
    pass "detected as non-interactive because a token was supplied"
else
    fail "expected NONINTERACTIVE=1, got: ${RESOLVE_OUTPUT}"
fi
if echo "${RESOLVE_OUTPUT}" | grep -q "SERVER_ADDRESS=grpc.app.vordfleet.dev"; then
    pass "fell back to the default server address"
else
    fail "expected the default server address, got: ${RESOLVE_OUTPUT}"
fi

echo "== fully non-interactive with no token fails fast instead of blocking =="
NOTOKEN_STATUS=0
NOTOKEN_OUTPUT="$(
    run_with_timeout bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        resolve_noninteractive
        HAS_TTY_OVERRIDE_NOTE="the source of NONINTERACTIVE here is env/flag absence plus (in CI) no controlling tty"
        resolve_registration_token
    ' </dev/null 2>&1
)" || NOTOKEN_STATUS=$?

if [ "${NOTOKEN_STATUS}" -eq 124 ]; then
    fail "blocked for ${TIMEOUT_SECS}s+ instead of failing fast on a missing token"
elif [ "${NOTOKEN_STATUS}" -eq 0 ]; then
    fail "expected a non-zero exit when no token is available non-interactively"
else
    pass "failed fast (exit ${NOTOKEN_STATUS}) instead of blocking on a tty read"
fi
if echo "${NOTOKEN_OUTPUT}" | grep -q "Registration token is required"; then
    pass "failed with the missing-token message, not an unrelated error"
else
    fail "expected the missing-token message, got: ${NOTOKEN_OUTPUT}"
fi

echo "== --help documents --port =="
if echo "${HELP_OUTPUT}" | grep -q -- "--port"; then
    pass "--help documents --port"
else
    fail "--help missing --port"
fi

echo "== --port without a value exits non-zero =="
if bash "${INSTALL_SH}" --port >/dev/null 2>&1; then
    fail "--port with no value should have failed"
else
    pass "--port with no value exits non-zero"
fi

echo "== --update rejects configuration flags instead of silently discarding them =="
# Asserting only a non-zero exit would pass even if the rejection were deleted: this suite runs as
# a normal user, and the root check further down would reject the command anyway. Grep the specific
# message so the test actually fails when the feature is reverted.
for combo in "--token tok" "--server host" "--port 12234"; do
    # shellcheck disable=SC2086
    UPDATE_OUTPUT="$(bash "${INSTALL_SH}" --update ${combo} 2>&1)" && UPDATE_STATUS=0 || UPDATE_STATUS=$?
    if [ "${UPDATE_STATUS}" -eq 0 ]; then
        fail "--update ${combo} should have failed"
    elif echo "${UPDATE_OUTPUT}" | grep -q "does not change configuration"; then
        pass "--update ${combo} is rejected by name"
    else
        fail "--update ${combo} failed for the wrong reason: ${UPDATE_OUTPUT}"
    fi
done

echo "== --server rejects a host:port value =="
SERVERPORT_OUTPUT="$(bash "${INSTALL_SH}" --server host.example.com:12234 2>&1)" && SERVERPORT_STATUS=0 || SERVERPORT_STATUS=$?
if [ "${SERVERPORT_STATUS}" -eq 0 ]; then
    fail "--server with a port should have failed"
else
    pass "--server with a port exits non-zero"
fi
if echo "${SERVERPORT_OUTPUT}" | grep -q -- "--port"; then
    pass "the host:port error names --port as the fix"
else
    fail "expected the error to point at --port, got: ${SERVERPORT_OUTPUT}"
fi

echo "== --server accepts a bare IPv6 literal =="
# A bare IPv6 literal must not be mistaken for host:port. Run as a non-root user, so reaching the
# root check is proof the address itself was accepted.
IPV6_OUTPUT="$(bash "${INSTALL_SH}" --server 2001:db8::1 --token tok 2>&1)" || true
if echo "${IPV6_OUTPUT}" | grep -q "must be run as root"; then
    pass "a bare IPv6 literal passes address validation"
else
    fail "expected the IPv6 address to be accepted, got: ${IPV6_OUTPUT}"
fi
IPV6_BRACKET_OUTPUT="$(bash "${INSTALL_SH}" --server '[2001:db8::1]' --token tok 2>&1)" || true
if echo "${IPV6_BRACKET_OUTPUT}" | grep -q "must be run as root"; then
    pass "a bracketed IPv6 literal passes address validation"
else
    fail "expected the bracketed IPv6 address to be accepted, got: ${IPV6_BRACKET_OUTPUT}"
fi

echo "== a supplied server address does not suppress the token prompt when a tty exists =="
# Regression guard: resolve_noninteractive used to treat VORD_SERVER_ADDRESS as evidence that
# prompting was impossible, so `install.sh --server foo` at a real terminal refused to ask for the
# missing token. Only a supplied token should suppress prompting. The tty state is injected so
# this exercises the interactive branch without allocating a pty.
SERVERONLY_OUTPUT="$(
    run_with_timeout bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        VORD_SERVER_ADDRESS="vord.example.com"
        resolve_noninteractive 1
        printf "NONINTERACTIVE=%s" "${NONINTERACTIVE}"
    ' </dev/null
)" || true
if echo "${SERVERONLY_OUTPUT}" | grep -q "NONINTERACTIVE=0"; then
    pass "server-only input still allows prompting for the token"
else
    fail "expected NONINTERACTIVE=0, got: ${SERVERONLY_OUTPUT}"
fi

echo "== a supplied token still suppresses prompting even with a tty =="
TOKENTTY_OUTPUT="$(
    run_with_timeout bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        VORD_REGISTRATION_TOKEN="tok"
        resolve_noninteractive 1
        printf "NONINTERACTIVE=%s" "${NONINTERACTIVE}"
    ' </dev/null
)" || true
if echo "${TOKENTTY_OUTPUT}" | grep -q "NONINTERACTIVE=1"; then
    pass "token-supplied input skips prompting"
else
    fail "expected NONINTERACTIVE=1, got: ${TOKENTTY_OUTPUT}"
fi

echo "== values that would corrupt or subvert the TOML config are rejected =="
# server_address and registration_token are interpolated into double-quoted TOML strings. A quote,
# backslash, or newline either breaks parsing (agent exits) or injects a key — allow_remote_commands
# being the dangerous one.
check_validator() {
    local fn="$1" value="$2" expect="$3" label="$4"
    local status=0
    bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        '"${fn}"' "$1"
    ' _ "${value}" >/dev/null 2>&1 || status=$?
    if [ "${expect}" = "accept" ] && [ "${status}" -eq 0 ]; then
        pass "${label}"
    elif [ "${expect}" = "reject" ] && [ "${status}" -ne 0 ]; then
        pass "${label}"
    else
        fail "${label} (${fn} returned ${status})"
    fi
}

check_validator valid_server_address "grpc.app.vordfleet.dev" accept "accepts a plain hostname"
check_validator valid_server_address "" reject "rejects an empty server address"
check_validator valid_server_address 'x"' reject "rejects a quote in the server address"
check_validator valid_server_address 'host.example.com:12234' reject "rejects a port in the server address"
check_validator valid_server_address "$(printf 'x\nallow_remote_commands = true')" reject "rejects a newline injection in the server address"
check_validator valid_server_address "192.168.1.100" accept "accepts an IPv4 address"
# Bare and bracketed IPv6 are both accepted; the agent brackets the bare form before dialling.
check_validator valid_server_address "2001:db8::1" accept "accepts a bare IPv6 literal"
check_validator valid_server_address "::1" accept "accepts the IPv6 loopback"
check_validator valid_server_address "[2001:db8::1]" accept "accepts a bracketed IPv6 literal"
check_validator valid_server_address "2001:db8::1%eth0" reject "rejects an IPv6 zone id"
check_validator valid_server_address '2001:db8::"1' reject "rejects a quote inside an IPv6 literal"
check_validator valid_token "abc123.XYZ_-~+/=" accept "accepts a realistic token charset"
check_validator valid_token "" reject "rejects an empty token"
check_validator valid_token 'tok"' reject "rejects a quote in the token"
check_validator valid_token 'tok\' reject "rejects a backslash in the token"
check_validator valid_token "$(printf 'tok\nallow_remote_commands = true')" reject "rejects a newline injection in the token"
check_validator valid_port "443" accept "accepts a valid port"
check_validator valid_port "12234" accept "accepts a self-hosted gRPC port"
check_validator valid_port "1" accept "accepts the lowest valid port"
check_validator valid_port "65535" accept "accepts the highest valid port"
check_validator valid_port "0" reject "rejects port 0"
check_validator valid_port "65536" reject "rejects a port above 65535"
check_validator valid_port "abc" reject "rejects a non-numeric port"
# A leading zero passes bash's numeric comparison but is illegal in TOML ("cannot have leading
# zeroes"), so the agent would crash-loop on a config the installer just declared valid.
check_validator valid_port "0443" reject "rejects a leading-zero port"
check_validator valid_port "007" reject "rejects a zero-padded port"
# `[ 99999999999999999999 -gt 65535 ]` exits 2 rather than true/false, which the caller reads as
# valid; the resulting value is also out of range for the agent's int64 port field.
check_validator valid_port "99999999999999999999" reject "rejects a port beyond int64"

echo "== the config-skip test ignores the package's commented-out placeholder =="
# The package postinstall drops a placeholder config on every fresh install. A bare `[ -f ]` test
# treated that as "already configured" and silently discarded the user's token, producing an agent
# that runs forever without registering.
PLACEHOLDER_DIR="$(mktemp -d)"
trap 'rm -rf "${PLACEHOLDER_DIR}"' EXIT
cat > "${PLACEHOLDER_DIR}/placeholder.toml" <<'PLACEHOLDER'
# Vord Agent Configuration
# server_address = "vord.example.com"
# server_port = 12234
# registration_token = "xxx"
PLACEHOLDER
cat > "${PLACEHOLDER_DIR}/configured.toml" <<'CONFIGURED'
server_address = "grpc.app.vordfleet.dev"
server_port = 443
use_tls = true
registration_token = "real-token"
CONFIGURED

check_config_has_token() {
    local file="$1" expect="$2" label="$3"
    local status=0
    bash -c '
        set -euo pipefail
        source "'"${INSTALL_SH}"'"
        CONFIG_FILE="$1"
        config_has_token
    ' _ "${file}" >/dev/null 2>&1 || status=$?
    if [ "${expect}" = "configured" ] && [ "${status}" -eq 0 ]; then
        pass "${label}"
    elif [ "${expect}" = "unconfigured" ] && [ "${status}" -ne 0 ]; then
        pass "${label}"
    else
        fail "${label} (config_has_token returned ${status})"
    fi
}

cat > "${PLACEHOLDER_DIR}/empty-token.toml" <<'EMPTYTOKEN'
server_address = "grpc.app.vordfleet.dev"
registration_token = ""
EMPTYTOKEN

check_config_has_token "${PLACEHOLDER_DIR}/placeholder.toml" unconfigured "the commented-out placeholder counts as unconfigured"
check_config_has_token "${PLACEHOLDER_DIR}/configured.toml" configured "a real token counts as configured"
check_config_has_token "${PLACEHOLDER_DIR}/does-not-exist.toml" unconfigured "a missing file counts as unconfigured"
# An explicitly empty token is not a configured host: treating it as one would make a re-run with a
# real --token silently keep the broken config.
check_config_has_token "${PLACEHOLDER_DIR}/empty-token.toml" unconfigured "an empty token counts as unconfigured"

echo
if [ "${FAILURES}" -eq 0 ]; then
    echo "All checks passed."
    exit 0
else
    echo "${FAILURES} check(s) failed."
    exit 1
fi
