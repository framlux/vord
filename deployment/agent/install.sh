#!/usr/bin/env bash
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Vord Agent installer — automates repo setup, package install, and configuration.
# Usage: curl -fsSL https://get.vordfleet.dev | sudo bash -s -- --token YOUR_TOKEN
# Or non-interactive via env vars:
#   VORD_SERVER_ADDRESS=grpc.app.vordfleet.dev VORD_REGISTRATION_TOKEN=xxx \
#     curl -fsSL https://get.vordfleet.dev | sudo bash
# Run with --help for the full flag reference.

set -euo pipefail

export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH}"

# Keep apt from opening a conffile or debconf prompt on an unrelated pending upgrade — the
# documented one-liner has no tty to answer one on, and the install would hang indefinitely.
export DEBIAN_FRONTEND=noninteractive

GPG_KEY_URL="https://apt.fury.io/framlux/gpg.key"
APT_REPO_URL="https://packages.framlux.io/apt/"
YUM_REPO_URL="https://packages.framlux.io/yum/"
KEYRING_PATH="/usr/share/keyrings/framlux-archive-keyring.gpg"
PACKAGE_NAME="vord-agent"
CONFIG_DIR="/etc/framlux"
CONFIG_FILE="${CONFIG_DIR}/vord-agent.toml"
DEFAULT_SERVER="grpc.app.vordfleet.dev"
DEFAULT_PORT=443

# --- Helpers ---

info() {
    printf "\033[1;34m==>\033[0m %s\n" "$1"
}

success() {
    printf "\033[1;32m==>\033[0m %s\n" "$1"
}

error() {
    printf "\033[1;31mERROR:\033[0m %s\n" "$1" >&2
}

usage() {
    cat <<EOF
Vord Agent installer

Usage:
  install.sh [OPTIONS]

Options:
  --token <TOKEN>     Registration token for this fleet.
                       (or set the VORD_REGISTRATION_TOKEN env var)
  --server <ADDRESS>  gRPC server hostname, IPv4, or IPv6 literal, without a port.
                       Default: ${DEFAULT_SERVER}
                       (or set the VORD_SERVER_ADDRESS env var)
  --port <PORT>       gRPC server port. Default: ${DEFAULT_PORT}
                       (or set the VORD_SERVER_PORT env var)
  --update            Upgrade an already-installed ${PACKAGE_NAME} package and exit.
  --help              Show this help text and exit.

Examples:
  curl -fsSL https://get.vordfleet.dev | sudo bash -s -- --token YOUR_TOKEN
  VORD_REGISTRATION_TOKEN=YOUR_TOKEN curl -fsSL https://get.vordfleet.dev | sudo bash
  install.sh --token YOUR_TOKEN --server vord.example.com --port 12234
  install.sh --token YOUR_TOKEN --server 2001:db8::1 --port 12234
EOF
}

# --- Environment Probes ---
#
# systemd is absent or inoperative often enough — container base images, chroot/Packer builds,
# WSL — that enable/start/status must not be fatal. The package is installed and configured
# either way; only the "start it now" step is genuinely unavailable.
#
# Pure enough to be sourced and called directly in tests — see deployment/agent/install_test.sh.
have_systemd() {
    command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]
}

# --- Value Validation ---
#
# SERVER_ADDRESS and REGISTRATION_TOKEN are interpolated into double-quoted TOML strings when the
# config is written. A value carrying a quote or backslash corrupts the file — the agent then
# fails to parse it and exits — and a value carrying a newline can inject an attacker-chosen key
# such as allow_remote_commands, which gates remote command execution on the host. Reject those
# outright rather than trying to escape them; no hostname or server-issued token needs them.

# Accepts a hostname, an IPv4 address, or an IPv6 literal (bare or bracketed). A bare IPv6 literal
# is told apart from the host:port mistake by its colon count: an address needs at least two,
# host:port has exactly one.
valid_server_address() {
    local value="$1"

    case "${value}" in
        \[*\])
            value="${value#\[}"
            value="${value%\]}"
            ;;
    esac

    case "${value}" in
        "") return 1 ;;
        *:*:*)
            # IPv6 literal. Zone IDs (fe80::1%eth0) are deliberately not accepted: the zone is
            # local to one host, so it cannot be part of a fleet-wide config.
            case "${value}" in
                *[!0-9A-Fa-f:.]*) return 1 ;;
            esac

            return 0
            ;;
        *:*) return 1 ;;
        *[!A-Za-z0-9._-]*) return 1 ;;
    esac

    return 0
}

# Prints the reason an address was rejected. Shared so the flag path and the interactive prompt
# give the same diagnosis.
report_invalid_server_address() {
    case "$1" in
        *:*:*)
            error "Invalid IPv6 address: '$1'."
            error "Expected a literal such as 2001:db8::1 or [2001:db8::1], with the port in --port."
            ;;
        *:*)
            error "Server address must not include a port: '$1'."
            error "Set the port with --port or VORD_SERVER_PORT, e.g. --port 12234"
            ;;
        *)
            error "Invalid server address: '$1'. Expected a hostname, IPv4 address, or IPv6 literal, e.g. ${DEFAULT_SERVER}."
            ;;
    esac
}

valid_port() {
    case "$1" in
        "" | *[!0-9]*) return 1 ;;
        # A leading zero parses as decimal in `[ -lt ]` but is illegal in TOML — BurntSushi rejects
        # "0443" as "cannot have leading zeroes" — so the agent would crash-loop on a config the
        # installer had just called valid.
        0[0-9]*) return 1 ;;
    esac
    # Reject over-long input before comparing: `[ 99999999999999999999 -gt 65535 ]` exits 2 rather
    # than true or false, which reads as "valid" in the caller and writes an out-of-int64 port.
    if [ "${#1}" -gt 5 ]; then
        return 1
    fi
    if [ "$1" -lt 1 ] || [ "$1" -gt 65535 ]; then
        return 1
    fi

    return 0
}

# Deliberately a deny-list rather than an allow-list: token formats are the server's to change,
# and wrongly rejecting a valid token is worse than accepting an unusual one. Only the characters
# that can actually break or subvert the TOML are refused.
valid_token() {
    case "$1" in
        "") return 1 ;;
        *[\"\\]*) return 1 ;;
        *[[:space:]]*) return 1 ;;
    esac

    return 0
}

# --- Package State ---

package_installed() {
    if [ "${PKG_MANAGER}" = "apt" ]; then
        dpkg-query -W -f='${Status}' "${PACKAGE_NAME}" 2>/dev/null | grep -q "install ok installed"
    else
        rpm -q "${PACKAGE_NAME}" >/dev/null 2>&1
    fi
}

# The package's own postinstall drops a placeholder ${CONFIG_FILE} containing nothing but
# commented-out examples on every fresh install, so a bare `[ -f ... ]` test would report each
# new host as already-configured and silently discard the token the user just supplied — leaving
# an agent that runs, never registers, and reports success. Only a file carrying an uncommented
# registration_token counts as configured.
config_has_token() {
    [ -f "${CONFIG_FILE}" ] && grep -qE '^[[:space:]]*registration_token[[:space:]]*=[[:space:]]*"[^"]+"' "${CONFIG_FILE}"
}

# --- Non-interactive Detection ---
#
# A tty is required to prompt for the server address or registration token. The documented
# one-liner (`curl ... | sudo bash`) has no tty, and neither do config-management tools
# (cloud-init, Ansible, Packer, `docker build`). Blocking on `read ... < /dev/tty` in that
# situation fails under `set -e` with a raw, confusing error — and by that point the package is
# already installed, leaving a half-installed host with no config. Detect the non-interactive
# case up front so we fall back to defaults (or fail with a clear message) instead.
#
# Only a supplied token suppresses prompting. The server address and port have usable defaults,
# so supplying either of those says nothing about whether we can still ask for the token — gating
# on them too would refuse to prompt for a missing token even at an interactive terminal.
#
# Pure enough to be sourced and called directly in tests without root or a package manager —
# see deployment/agent/install_test.sh. Takes an optional pre-computed tty state so tests can
# exercise the interactive branch without a real pty; production callers pass nothing and let it
# probe.
resolve_noninteractive() {
    HAS_TTY="${1:-}"
    if [ -z "${HAS_TTY}" ]; then
        HAS_TTY=0
        if { exec 3<>/dev/tty; } 2>/dev/null; then
            HAS_TTY=1
            exec 3>&-
        fi
    fi

    NONINTERACTIVE=0
    if [ -n "${VORD_REGISTRATION_TOKEN:-}" ] || [ "${HAS_TTY}" -eq 0 ]; then
        # Either the caller already told us enough to skip prompting, or there is nothing to
        # prompt on. Either way, don't try.
        NONINTERACTIVE=1
    fi
}

# Resolves SERVER_ADDRESS from VORD_SERVER_ADDRESS, prompting only when interactive.
# Requires resolve_noninteractive to have set NONINTERACTIVE first.
resolve_server_address() {
    SERVER_ADDRESS="${VORD_SERVER_ADDRESS:-}"
    if [ -z "${SERVER_ADDRESS}" ]; then
        if [ "${NONINTERACTIVE}" -eq 1 ]; then
            SERVER_ADDRESS="${DEFAULT_SERVER}"
        else
            printf "\n" > /dev/tty
            printf "Enter the server address [%s]: " "${DEFAULT_SERVER}" > /dev/tty
            read -r SERVER_ADDRESS < /dev/tty
            if [ -z "${SERVER_ADDRESS}" ]; then
                SERVER_ADDRESS="${DEFAULT_SERVER}"
            fi
        fi
    fi

    if ! valid_server_address "${SERVER_ADDRESS}"; then
        report_invalid_server_address "${SERVER_ADDRESS}"

        return 1
    fi

    return 0
}

# Resolves SERVER_PORT from VORD_SERVER_PORT. Never prompts — the default is correct for the
# hosted service and self-hosters pass --port or the env var.
resolve_server_port() {
    SERVER_PORT="${VORD_SERVER_PORT:-${DEFAULT_PORT}}"

    if ! valid_port "${SERVER_PORT}"; then
        error "Invalid server port: '${SERVER_PORT}'. Expected a number between 1 and 65535."

        return 1
    fi

    return 0
}

# Resolves REGISTRATION_TOKEN from VORD_REGISTRATION_TOKEN, prompting only when interactive and
# failing fast (no prompt attempt) when non-interactive with nothing supplied.
# Requires resolve_noninteractive to have set NONINTERACTIVE first.
resolve_registration_token() {
    REGISTRATION_TOKEN="${VORD_REGISTRATION_TOKEN:-}"
    if [ -z "${REGISTRATION_TOKEN}" ]; then
        if [ "${NONINTERACTIVE}" -eq 1 ]; then
            error "Registration token is required. Pass --token <TOKEN> or set VORD_REGISTRATION_TOKEN."

            return 1
        fi
        printf "Enter your registration token: " > /dev/tty
        read -r REGISTRATION_TOKEN < /dev/tty
        if [ -z "${REGISTRATION_TOKEN}" ]; then
            error "Registration token is required. You can find it in the Vord Fleet dashboard under Machines > Register."

            return 1
        fi
    fi

    if ! valid_token "${REGISTRATION_TOKEN}"; then
        error "Invalid registration token: it must not contain quotes, backslashes, or whitespace."

        return 1
    fi

    return 0
}

# --- Installation Steps ---

# curl (to fetch the signing key), gpg (to dearmor it), and ca-certificates (for that HTTPS fetch
# and for the agent's own TLS dial at runtime, which uses the system trust store) are all assumed
# by the steps below, and minimal cloud and container images ship none of them. Installing them up
# front turns an obscure `gpg: command not found` abort into an ordinary package install.
install_prerequisites() {
    info "Ensuring installer prerequisites are present..."

    if [ "${PKG_MANAGER}" = "apt" ]; then
        apt-get update -qq
        apt-get install -y -qq curl gnupg ca-certificates
    else
        "${PKG_MANAGER}" install -y -q ca-certificates
    fi
}

setup_repository() {
    info "Importing Framlux GPG key..."

    if [ "${PKG_MANAGER}" = "apt" ]; then
        # --batch --yes: `gpg --dearmor -o FILE` refuses to overwrite an existing FILE and, with
        # no tty to prompt on, dies — which would make every re-run of the one-liner fail here,
        # exactly when someone is retrying after an earlier failure. Dearmor to a temp file and
        # install it into place so a download truncated mid-stream can never leave a corrupt
        # keyring behind, which would break apt for every other repository on the host.
        local tmp_keyring
        tmp_keyring="$(mktemp)"
        # shellcheck disable=SC2064  # expand tmp_keyring now; it is gone by the time the trap runs
        trap "rm -f '${tmp_keyring}'" EXIT
        curl -fsSL "${GPG_KEY_URL}" | gpg --batch --yes --dearmor -o "${tmp_keyring}"
        install -m 0644 "${tmp_keyring}" "${KEYRING_PATH}"
        rm -f "${tmp_keyring}"
        trap - EXIT
    else
        rpm --import "${GPG_KEY_URL}"
    fi

    info "Adding Framlux package repository..."

    if [ "${PKG_MANAGER}" = "apt" ]; then
        cat > /etc/apt/sources.list.d/framlux.list <<EOF
deb [signed-by=${KEYRING_PATH}] ${APT_REPO_URL} * *
EOF
    else
        # The published RPMs carry no package signature, so gpgcheck=1 would fail every install
        # with "package is not signed". The repository metadata IS signed
        # (repodata/repomd.xml.asc), so verify at that layer instead — this is Gemfury's
        # documented configuration, and it still chains trust to the key imported above.
        cat > /etc/yum.repos.d/framlux.repo <<EOF
[framlux]
name=Framlux Packages
baseurl=${YUM_REPO_URL}
enabled=1
gpgcheck=0
repo_gpgcheck=1
gpgkey=${GPG_KEY_URL}
EOF
    fi

    info "Updating package cache..."

    if [ "${PKG_MANAGER}" = "apt" ]; then
        apt-get update -qq
    else
        # -y is required, not cosmetic: repo_gpgcheck makes dnf import the repo key into its own
        # per-repo keyring, and that import is an interactive confirmation which defaults to N.
        # Without -y a piped install answers N and dies on "repomd.xml GPG signature verification
        # error", taking every rpm-family install and --update with it. (`rpm --import` above
        # populates the rpmdb, which is a separate trust store dnf does not consult here.)
        "${PKG_MANAGER}" makecache -y -q
    fi
}

start_agent() {
    if ! have_systemd; then
        info "systemd is not available here — skipping service start."
        info "Start ${PACKAGE_NAME} with your init system, or on first boot, to begin reporting."

        return 0
    fi

    info "Enabling and starting ${PACKAGE_NAME}..."

    systemctl enable "${PACKAGE_NAME}"
    # The package postinstall already started the agent — before this script had written the
    # config — so `start` would be a no-op against a process still holding the placeholder.
    # Restart to make it re-read the file. reset-failed first: repeated registration failures can
    # trip StartLimitBurst, and a rate-limited unit refuses to start at all.
    systemctl reset-failed "${PACKAGE_NAME}" 2>/dev/null || true
    systemctl restart "${PACKAGE_NAME}"
}

main() {

# --- Flag Parsing (overrides the VORD_* env vars) ---

UPDATE_ONLY=0
CONFIG_FLAGS_GIVEN=0

while [ $# -gt 0 ]; do
    case "$1" in
        --token)
            if [ $# -lt 2 ]; then
                error "--token requires a value"
                exit 1
            fi
            VORD_REGISTRATION_TOKEN="$2"
            CONFIG_FLAGS_GIVEN=1
            shift 2
            ;;
        --server)
            if [ $# -lt 2 ]; then
                error "--server requires a value"
                exit 1
            fi
            VORD_SERVER_ADDRESS="$2"
            CONFIG_FLAGS_GIVEN=1
            shift 2
            ;;
        --port)
            if [ $# -lt 2 ]; then
                error "--port requires a value"
                exit 1
            fi
            VORD_SERVER_PORT="$2"
            CONFIG_FLAGS_GIVEN=1
            shift 2
            ;;
        --update)
            UPDATE_ONLY=1
            shift
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            error "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

# --update only upgrades the package; it never rewrites the config. Accepting configuration flags
# alongside it and silently discarding them is worse than refusing.
if [ "${UPDATE_ONLY}" -eq 1 ] && [ "${CONFIG_FLAGS_GIVEN}" -eq 1 ]; then
    error "--update upgrades the package only and does not change configuration."
    error "Drop --token/--server/--port, or edit ${CONFIG_FILE} and restart ${PACKAGE_NAME}."
    exit 1
fi

# A host:port value in --server is the natural mistake, and it fails silently: the agent would dial
# "host:port:443". Validate here so it is caught before anything is installed. Skipped for --update,
# which never reads the address at all — failing there on an inherited env var would be noise.
if [ "${UPDATE_ONLY}" -eq 0 ] && [ -n "${VORD_SERVER_ADDRESS:-}" ]; then
    if ! valid_server_address "${VORD_SERVER_ADDRESS}"; then
        report_invalid_server_address "${VORD_SERVER_ADDRESS}"
        exit 1
    fi
fi

# --- Preflight Checks ---

if [ "${EUID:-$(id -u)}" -ne 0 ]; then
    error "This script must be run as root. Try: sudo bash install.sh"
    exit 1
fi

PKG_MANAGER=""
if command -v apt-get >/dev/null 2>&1; then
    PKG_MANAGER="apt"
elif command -v dnf >/dev/null 2>&1; then
    PKG_MANAGER="dnf"
elif command -v yum >/dev/null 2>&1; then
    PKG_MANAGER="yum"
else
    error "Unsupported system: neither apt-get, dnf, nor yum found."
    exit 1
fi

info "Detected package manager: ${PKG_MANAGER}"

# --- Update-Only Path ---

if [ "${UPDATE_ONLY}" -eq 1 ]; then
    if ! package_installed; then
        error "${PACKAGE_NAME} is not installed. Run the installer without --update first."
        exit 1
    fi

    install_prerequisites
    # Refresh the signing key and repo definition before upgrading. If either has rotated since
    # this host was installed, the upgrade fails, and --update is the only command most operators
    # will think to run.
    setup_repository

    info "Upgrading ${PACKAGE_NAME}..."
    if [ "${PKG_MANAGER}" = "apt" ]; then
        apt-get install -y -qq --only-upgrade "${PACKAGE_NAME}"
    else
        "${PKG_MANAGER}" upgrade -y -q "${PACKAGE_NAME}"
    fi

    if have_systemd; then
        systemctl restart "${PACKAGE_NAME}"
    fi

    success "${PACKAGE_NAME} upgraded successfully."
    exit 0
fi

# --- Resolve Configuration (before touching the package manager, so a resolution failure —
#     e.g. no token available in a non-interactive run — never leaves a half-installed host) ---

if config_has_token; then
    info "Existing configuration found at ${CONFIG_FILE}, will keep it."
else
    resolve_noninteractive
    if ! resolve_server_address; then
        exit 1
    fi
    if ! resolve_server_port; then
        exit 1
    fi
    if ! resolve_registration_token; then
        exit 1
    fi
fi

install_prerequisites
setup_repository

# --- Install the Agent ---

info "Installing ${PACKAGE_NAME}..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    apt-get install -y -qq "${PACKAGE_NAME}"
else
    "${PKG_MANAGER}" install -y -q "${PACKAGE_NAME}"
fi

success "${PACKAGE_NAME} installed successfully."

# --- Write Configuration ---
#
# SERVER_ADDRESS/SERVER_PORT/REGISTRATION_TOKEN were already resolved above, before the package
# manager was touched, so there is nothing left to prompt for or fail on here. Re-test for the
# token rather than reusing the earlier result: the package's postinstall has run in between and
# has created its placeholder config.

if config_has_token; then
    info "Existing configuration found at ${CONFIG_FILE}, skipping configuration."
else
    info "Writing configuration to ${CONFIG_FILE}..."

    mkdir -p "${CONFIG_DIR}"
    # Keep whatever was here — the package placeholder, or a config an operator hand-edited
    # without a token — rather than discarding it silently.
    if [ -f "${CONFIG_FILE}" ]; then
        cp -p "${CONFIG_FILE}" "${CONFIG_FILE}.bak"
    fi
    cat > "${CONFIG_FILE}" <<EOF
server_address = "${SERVER_ADDRESS}"
server_port = ${SERVER_PORT}
use_tls = true
registration_token = "${REGISTRATION_TOKEN}"
EOF
    chmod 0600 "${CONFIG_FILE}"
fi

# The data directory is created by the package at 0700 and managed by the unit's StateDirectory=,
# so the installer deliberately does not touch it.

# --- Enable and Start the Agent ---

start_agent

printf "\n"
if have_systemd; then
    success "Vord Agent is installed and running!"
    printf "\n"
    # `systemctl status` exits 3 for an inactive unit. It is the last command in the script, so
    # leaving it unguarded would make a successful install report failure to the caller.
    systemctl status "${PACKAGE_NAME}" --no-pager || true
else
    success "Vord Agent is installed and configured."
fi

}

# Only run when executed directly (e.g. `bash install.sh` or piped from curl), not when sourced.
# Sourcing (as deployment/agent/install_test.sh does) loads the functions above — including the
# tty/non-interactive resolution and validation logic — without touching the package manager or
# requiring root, so that logic can be exercised directly in tests.
#
# `return` outside a function succeeds only in a sourced script, which makes this the one test
# that holds for every invocation form. Comparing "${BASH_SOURCE[0]}" to "${0}" does NOT: when the
# script arrives on stdin — `curl -fsSL https://get.vordfleet.dev | sudo bash`, the documented
# install path — BASH_SOURCE is empty, so under `set -u` that comparison aborts the script with
# "BASH_SOURCE[0]: unbound variable" and the installer never runs at all.
if ! (return 0 2>/dev/null); then
    main "$@"
fi
