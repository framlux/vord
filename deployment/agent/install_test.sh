#!/usr/bin/env bash
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Minimal manual smoke test for install.sh's flag parsing. Run directly:
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

echo "== bash -n syntax check =="
if bash -n "${INSTALL_SH}"; then
    pass "syntax is valid"
else
    fail "syntax check failed"
fi

echo "== --help exits 0 and prints usage without requiring root =="
HELP_OUTPUT="$(bash "${INSTALL_SH}" --help)"
HELP_STATUS=$?
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

echo
if [ "${FAILURES}" -eq 0 ]; then
    echo "All checks passed."
    exit 0
else
    echo "${FAILURES} check(s) failed."
    exit 1
fi
