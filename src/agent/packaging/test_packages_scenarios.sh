#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Runs one scenario inside a test container and checks the result. test_packages.sh builds the
# packages, starts a fresh container per scenario, and mounts this directory and the packages into
# it. Run directly:
#   sh test_packages_scenarios.sh list
#   sh test_packages_scenarios.sh <scenario>
#
# Most scenarios drive the distro's real package manager (apt-get on Debian, dnf on Rocky Linux)
# through the packages' real maintainer scripts and check the state the vord-agent unit ends up in.
# systemd's enablement logic underneath is real too; only the verbs that need a running manager are
# faked (see test_systemctl_shim.sh), and that shim logs every call it receives. script_arguments
# is the exception: it runs each maintainer script directly with each argument form a package
# manager passes and checks the exact systemctl calls it makes.
#
# The packages come from PKG_DIR:
#   released  the last shipped agent, built from its own tagged scripts
#   current   the working tree's scripts, at a version above released
#   next      the working tree's scripts, at a version above current

set -eu

SCENARIOS="fresh_install upgrade_from_released upgrade downgrade remove remove_reinstall masked_fresh_install masked_upgrade script_arguments"

if [ "${1:-}" = "list" ]; then
    for scenario in ${SCENARIOS}; do
        echo "${scenario}"
    done
    exit 0
fi

UNIT="vord-agent"
PKG_DIR="${PKG_DIR:-/pkgs}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CALL_LOG="/run/systemctl-shim/calls"
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

# --- systemctl call log ---
#
# The shim appends the arguments of every systemctl call to CALL_LOG, one call per line.

clear_calls() {
    mkdir -p "$(dirname "${CALL_LOG}")"
    : > "${CALL_LOG}"
}

# The calls since the last clear_calls on one line, in order and separated by semicolons.
recorded_calls() {
    joined=""
    while IFS= read -r line; do
        joined="${joined:+${joined};}${line}"
    done < "${CALL_LOG}"
    printf '%s' "${joined}"
}

# How many calls since the last clear_calls started or restarted a unit.
activation_count() {
    grep -cE '^(start|restart) ' "${CALL_LOG}" || true
}

# --- Package manager ---
#
# The failure is recorded instead of aborting so the final state is still reported. Three things
# make a transaction fail:
#   - A non-zero exit. apt-get exits non-zero when a maintainer script fails; dnf does not, and
#     reports a failing scriptlet only in its output with an exit status of 0.
#   - A scriptlet error in the output, which is how a failing rpm scriptlet shows. rpm words it
#     "scriptlet failed" and dnf "Error in POSTTRANS scriptlet".
#   - The wrong number of start or restart calls. A package transaction must start the unit exactly
#     once, so that a script that also handles the service when another one already does is caught
#     as a double restart.
#
# package_manager <expected start/restart calls> <package manager command...>

package_manager() {
    expected_activations="$1"
    shift

    clear_calls
    status=0
    output="$("$@" 2>&1)" || status=$?
    printf '%s\n' "${output}"

    if [ "${status}" -ne 0 ]; then
        printf '  FAIL package manager exited %s: %s\n' "${status}" "$*"
        FAILED=1
    fi
    if printf '%s\n' "${output}" | grep -Eq 'Error in .* scriptlet|scriptlet failed'; then
        printf '  FAIL a package scriptlet failed: %s\n' "$*"
        FAILED=1
    fi

    activations="$(activation_count)"
    if [ "${activations}" -ne "${expected_activations}" ]; then
        printf '  FAIL expected %s start or restart call(s) but saw %s: %s\n' \
            "${expected_activations}" "${activations}" "$(recorded_calls)"
        FAILED=1
    fi
}

# Installs the package, or upgrades to it when an older version is installed.
pkg_install() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager 1 apt-get install -y -q "$1"
    else
        package_manager 1 dnf install -y -q --disablerepo='*' "$1"
    fi
}

pkg_downgrade() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager 1 apt-get install -y -q --allow-downgrades "$1"
    else
        package_manager 1 dnf downgrade -y -q --disablerepo='*' "$1"
    fi
}

# Removes the package but, on Debian, keeps its configuration (apt-get remove, not purge).
pkg_remove() {
    if [ "${FAMILY}" = "deb" ]; then
        package_manager 0 apt-get remove -y -q "${UNIT}"
    else
        package_manager 0 dnf remove -y -q --disablerepo='*' "${UNIT}"
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

# Every argument form dpkg and rpm pass to the maintainer scripts, with the exact systemctl calls
# the script must make for it, in order. The package managers only ever produce some of these
# (a failed upgrade, a conflict removal), so running the scripts directly is the only way to cover
# all of them. The arguments follow dpkg's maintainer script protocol and rpm's scriptlet counts.
#
# script | arguments | systemctl calls, separated by semicolons
ARGUMENT_CASES='
preremove.sh|remove|stop vord-agent;disable vord-agent
preremove.sh|upgrade 4.0.1|
preremove.sh|deconfigure in-favour other-package 1.0 removing vord-agent 4.0.0|
preremove.sh|failed-upgrade 4.0.1|
preremove.sh|0|stop vord-agent;disable vord-agent
preremove.sh|1|
postinstall.sh|configure|daemon-reload;enable vord-agent;start vord-agent
postinstall.sh|configure 3.3.0|daemon-reload;enable vord-agent;restart vord-agent
postinstall.sh|abort-upgrade 3.3.0|start vord-agent
postinstall.sh|abort-remove|enable vord-agent;start vord-agent
postinstall.sh|abort-deconfigure in-favour other-package 1.0|enable vord-agent;start vord-agent
postinstall.sh|triggered vord-agent|
postinstall.sh|1|daemon-reload
postinstall.sh|2|daemon-reload
posttrans.sh|0|daemon-reload;enable vord-agent;restart vord-agent
posttrans.sh|1|daemon-reload;enable vord-agent;restart vord-agent
'

scenario_script_arguments() {
    while IFS='|' read -r script arguments expected; do
        if [ -z "${script}" ]; then
            continue
        fi

        rm -f /etc/framlux/vord-agent.toml
        clear_calls
        # The arguments are meant to be split into separate words.
        # shellcheck disable=SC2086
        sh "${SCRIPT_DIR}/${script}" ${arguments} >/dev/null 2>&1 || {
            printf '  FAIL %s %s exited non-zero\n' "${script}" "${arguments}"
            FAILED=1
        }

        actual="$(recorded_calls)"
        if [ "${actual}" = "${expected}" ]; then
            printf '  ok   %s %s: %s\n' "${script}" "${arguments}" "${actual:-no calls}"
        else
            printf '  FAIL %s %s: expected [%s] but made [%s]\n' "${script}" "${arguments}" "${expected}" "${actual}"
            FAILED=1
        fi

        # The default config is written whatever the operation, so every argument form must leave
        # it behind.
        if [ "${script}" = "postinstall.sh" ] && [ ! -f /etc/framlux/vord-agent.toml ]; then
            printf '  FAIL %s %s did not write the default config\n' "${script}" "${arguments}"
            FAILED=1
        fi
    done <<CASES
${ARGUMENT_CASES}
CASES

    FINAL_STATE="not applicable"
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
FINAL_STATE=""
"scenario_${scenario}"
printf 'FINAL %s\n' "${FINAL_STATE:-$(enablement) $(activity)}"

[ "${FAILED}" -eq 0 ]
