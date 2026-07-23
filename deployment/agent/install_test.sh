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
    ' </dev/null
)" || NOTOKEN_STATUS=$?

if [ "${NOTOKEN_STATUS}" -eq 124 ]; then
    fail "blocked for ${TIMEOUT_SECS}s+ instead of failing fast on a missing token"
elif [ "${NOTOKEN_STATUS}" -eq 0 ]; then
    fail "expected a non-zero exit when no token is available non-interactively"
else
    pass "failed fast (exit ${NOTOKEN_STATUS}) instead of blocking on a tty read"
fi

echo
if [ "${FAILURES}" -eq 0 ]; then
    echo "All checks passed."
    exit 0
else
    echo "${FAILURES} check(s) failed."
    exit 1
fi
