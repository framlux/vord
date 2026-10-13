#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Stands in for /usr/bin/systemctl inside the package test containers (test_packages.sh), which
# have systemd's tools installed but no running systemd. enable, disable, is-enabled, mask and
# unmask go straight to the real binary: they only manage symlinks, work without a running
# manager, and are exactly the calls whose outcome the tests are about. The verbs that need a live
# manager are faked: start and restart record that the unit is active, stop clears it, and
# is-active reads the record back. A masked unit refuses to start, as it does under real systemd.
# The real binary is kept beside this one as systemctl.real.

set -eu

REAL="/usr/bin/systemctl.real"
STATE_DIR="/run/systemctl-shim"

verb=""
unit=""
for arg in "$@"; do
    case "${arg}" in
        -*) ;;
        *)
            if [ -z "${verb}" ]; then
                verb="${arg}"
            else
                unit="${arg%.service}"
            fi
            ;;
    esac
done

case "${verb}" in
    start | restart)
        if [ "$("${REAL}" is-enabled "${unit}" 2>/dev/null || true)" = "masked" ]; then
            echo "Failed to ${verb} ${unit}.service: Unit ${unit}.service is masked." >&2
            exit 1
        fi
        mkdir -p "${STATE_DIR}"
        : > "${STATE_DIR}/${unit}.active"
        ;;
    stop)
        rm -f "${STATE_DIR}/${unit}.active"
        ;;
    is-active)
        if [ -f "${STATE_DIR}/${unit}.active" ]; then
            echo "active"
        else
            echo "inactive"
            exit 3
        fi
        ;;
    daemon-reload | reset-failed)
        ;;
    *)
        exec "${REAL}" "$@"
        ;;
esac
