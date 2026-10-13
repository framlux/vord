#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Runs one package scenario inside a test container and checks the state the vord-agent unit ends
# up in. test_packages.sh builds the packages, starts a fresh container per scenario, and mounts
# this directory and the packages into it. Run directly:
#   sh test_packages_scenarios.sh list
#   sh test_packages_scenarios.sh <scenario>
#
# Every scenario drives the distro's real package manager (apt-get on Debian, dnf on Rocky Linux)
# through the packages' real maintainer scripts. systemd's enablement logic underneath is real
# too; only the verbs that need a running manager are faked (see test_systemctl_shim.sh).
#
# The packages come from PKG_DIR:
#   released  the last shipped agent, built from its own tagged scripts
#   current   the working tree's scripts, at a version above released
#   next      the working tree's scripts, at a version above current

set -eu

SCENARIOS="fresh_install upgrade_from_released upgrade downgrade remove remove_reinstall masked_fresh_install masked_upgrade"

if [ "${1:-}" = "list" ]; then
    for scenario in ${SCENARIOS}; do
        echo "${scenario}"
    done
    exit 0
fi

UNIT="vord-agent"
PKG_DIR="${PKG_DIR:-/pkgs}"
FAILED=0

if command -v dpkg >/dev/null 2>&1; then
    FAMILY="deb"
else
    FAMILY="rpm"
fi
RELEASED="${PKG_DIR}/released.${FAMILY}"
CURRENT="${PKG_DIR}/current.${FAMILY}"
NEXT="${PKG_DIR}/next.${FAMILY}"

export DEBIAN_FRONTEND=noninteractive

# --- Package manager ---
#
# A failing maintainer script makes the package manager exit non-zero, so a script that errors out
# is caught here rather than only by the state checks. The failure is recorded instead of aborting
# so the final state is still reported.

package_manager() {
    if ! "$@"; then
        printf '  FAIL package manager exited non-zero: %s\n' "$*"
        FAILED=1
    fi
}

# Installs the package, or upgrades to it when an older version is installed.
pkg_install() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager apt-get install -y -q "$1"
    else
        package_manager dnf install -y -q --disablerepo='*' "$1"
    fi
}

pkg_downgrade() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager apt-get install -y -q --allow-downgrades "$1"
    else
        package_manager dnf downgrade -y -q --disablerepo='*' "$1"
    fi
}

# Removes the package but, on Debian, keeps its configuration (apt-get remove, not purge).
pkg_remove() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager apt-get remove -y -q "${UNIT}"
    else
        package_manager dnf remove -y -q --disablerepo='*' "${UNIT}"
    fi
}

# --- Observed state ---

# enabled, masked, or disabled. A wants/ link left behind by a unit that was never disabled is
# reported as stale-link: is-enabled cannot see it once the unit file is gone, but it would
# resurrect the unit if the package came back.
enablement() {
    state="$(systemctl is-enabled "${UNIT}" 2>/dev/null || true)"
    case "${state}" in
        enabled | masked)
            printf '%s' "${state}"
            ;;
        *)
            if [ -n "$(find /etc/systemd/system -name "${UNIT}.service" 2>/dev/null)" ]; then
                printf 'stale-link'
            else
                printf 'disabled'
            fi
            ;;
    esac
}

activity() {
    if systemctl is-active "${UNIT}" >/dev/null 2>&1; then
        printf 'active'
    else
        printf 'inactive'
    fi
}

# expect <enablement> <activity|any> <what is being checked>
expect() {
    got_enablement="$(enablement)"
    got_activity="$(activity)"
    if [ "${got_enablement}" = "$1" ] && { [ "$2" = "any" ] || [ "${got_activity}" = "$2" ]; }; then
        printf '  ok   %s: %s, %s\n' "$3" "${got_enablement}" "${got_activity}"
    else
        printf '  FAIL %s: expected %s, %s but found %s, %s\n' "$3" "$1" "$2" "${got_enablement}" "${got_activity}"
        FAILED=1
    fi
}

# --- Scenarios ---

scenario_fresh_install() {
    pkg_install "${CURRENT}"
    expect enabled active "after a fresh install"
}

scenario_upgrade_from_released() {
    pkg_install "${RELEASED}"
    expect enabled active "with the released package installed"
    pkg_install "${CURRENT}"
    expect enabled active "after upgrading from the released package"
}

scenario_upgrade() {
    pkg_install "${CURRENT}"
    expect enabled active "with the current package installed"
    pkg_install "${NEXT}"
    expect enabled active "after upgrading to the next version"
}

scenario_downgrade() {
    pkg_install "${CURRENT}"
    expect enabled active "with the current package installed"
    pkg_downgrade "${RELEASED}"
    expect enabled active "after downgrading to the released package"
}

scenario_remove() {
    pkg_install "${CURRENT}"
    expect enabled active "with the current package installed"
    pkg_remove
    expect disabled inactive "after removing the package"
}

scenario_remove_reinstall() {
    pkg_install "${CURRENT}"
    pkg_remove
    expect disabled inactive "after removing the package"
    pkg_install "${CURRENT}"
    expect enabled active "after installing it again"
}

scenario_masked_fresh_install() {
    systemctl mask "${UNIT}"
    pkg_install "${CURRENT}"
    expect masked inactive "after installing over a masked unit"
}

scenario_masked_upgrade() {
    pkg_install "${CURRENT}"
    systemctl mask "${UNIT}"
    pkg_install "${NEXT}"
    expect masked any "after upgrading a masked unit"
}

scenario="${1:-}"
case " ${SCENARIOS} " in
    *" ${scenario} "*) ;;
    *)
        echo "usage: $0 list | <scenario>" >&2
        exit 2
        ;;
esac

echo "scenario ${scenario} (${FAMILY})"
"scenario_${scenario}"
printf 'FINAL %s %s\n' "$(enablement)" "$(activity)"

[ "${FAILED}" -eq 0 ]
