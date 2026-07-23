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

GPG_KEY_URL="https://apt.fury.io/framlux/gpg.key"
APT_REPO_URL="https://packages.framlux.io/apt/"
YUM_REPO_URL="https://packages.framlux.io/yum/"
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
  --server <ADDRESS>  gRPC server address. Default: ${DEFAULT_SERVER}
                       (or set the VORD_SERVER_ADDRESS env var)
  --update            Upgrade an already-installed ${PACKAGE_NAME} package and exit.
  --help              Show this help text and exit.

Examples:
  curl -fsSL https://get.vordfleet.dev | sudo bash -s -- --token YOUR_TOKEN
  VORD_REGISTRATION_TOKEN=YOUR_TOKEN curl -fsSL https://get.vordfleet.dev | sudo bash
EOF
}

# --- Flag Parsing (overrides the VORD_REGISTRATION_TOKEN / VORD_SERVER_ADDRESS env vars) ---

UPDATE_ONLY=0

while [ $# -gt 0 ]; do
    case "$1" in
        --token)
            if [ $# -lt 2 ]; then
                error "--token requires a value"
                exit 1
            fi
            VORD_REGISTRATION_TOKEN="$2"
            shift 2
            ;;
        --server)
            if [ $# -lt 2 ]; then
                error "--server requires a value"
                exit 1
            fi
            VORD_SERVER_ADDRESS="$2"
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
    info "Upgrading ${PACKAGE_NAME}..."
    if [ "${PKG_MANAGER}" = "apt" ]; then
        apt-get update -qq
        apt-get install -y -qq --only-upgrade "${PACKAGE_NAME}"
    else
        "${PKG_MANAGER}" upgrade -y -q "${PACKAGE_NAME}"
    fi
    success "${PACKAGE_NAME} upgraded successfully."
    systemctl restart "${PACKAGE_NAME}"
    exit 0
fi

# --- Import GPG Key ---

info "Importing Framlux GPG key..."

KEYRING_PATH="/usr/share/keyrings/framlux-archive-keyring.gpg"

if [ "${PKG_MANAGER}" = "apt" ]; then
    curl -fsSL "${GPG_KEY_URL}" | gpg --dearmor -o "${KEYRING_PATH}"
    chmod 0644 "${KEYRING_PATH}"
else
    rpm --import "${GPG_KEY_URL}"
fi

# --- Add Package Repository ---

info "Adding Framlux package repository..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    cat > /etc/apt/sources.list.d/framlux.list <<EOF
deb [signed-by=${KEYRING_PATH}] ${APT_REPO_URL} * *
EOF
else
    cat > /etc/yum.repos.d/framlux.repo <<EOF
[framlux]
name=Framlux Packages
baseurl=${YUM_REPO_URL}
enabled=1
gpgcheck=1
gpgkey=${GPG_KEY_URL}
EOF
fi

# --- Update Package Cache ---

info "Updating package cache..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    apt-get update -qq
else
    ${PKG_MANAGER} makecache -q
fi

# --- Install the Agent ---

info "Installing ${PACKAGE_NAME}..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    apt-get install -y -qq "${PACKAGE_NAME}"
else
    ${PKG_MANAGER} install -y -q "${PACKAGE_NAME}"
fi

success "${PACKAGE_NAME} installed successfully."

# --- Configuration (env vars or interactive prompts) ---

if [ -f "${CONFIG_FILE}" ]; then
    info "Existing configuration found at ${CONFIG_FILE}, skipping configuration."
else
    SERVER_ADDRESS="${VORD_SERVER_ADDRESS:-}"
    if [ -z "${SERVER_ADDRESS}" ]; then
        printf "\n" > /dev/tty
        printf "Enter the server address [%s]: " "${DEFAULT_SERVER}" > /dev/tty
        read -r SERVER_ADDRESS < /dev/tty
        if [ -z "${SERVER_ADDRESS}" ]; then
            SERVER_ADDRESS="${DEFAULT_SERVER}"
        fi
    fi

    REGISTRATION_TOKEN="${VORD_REGISTRATION_TOKEN:-}"
    if [ -z "${REGISTRATION_TOKEN}" ]; then
        printf "Enter your registration token: " > /dev/tty
        read -r REGISTRATION_TOKEN < /dev/tty
        if [ -z "${REGISTRATION_TOKEN}" ]; then
            error "Registration token is required. You can find it in the Vord Fleet dashboard under Machines > Register."
            exit 1
        fi
    fi

    # --- Write Configuration ---

    info "Writing configuration to ${CONFIG_FILE}..."

    mkdir -p "${CONFIG_DIR}"
    cat > "${CONFIG_FILE}" <<EOF
server_address = "${SERVER_ADDRESS}"
server_port = ${DEFAULT_PORT}
use_tls = true
registration_token = "${REGISTRATION_TOKEN}"
EOF
    chmod 0600 "${CONFIG_FILE}"
fi

# --- Create Data Directory ---

info "Creating data directory..."
mkdir -p /var/lib/vord-agent
chmod 0750 /var/lib/vord-agent

# --- Enable and Start the Agent ---

info "Enabling and starting ${PACKAGE_NAME}..."

systemctl enable "${PACKAGE_NAME}"
systemctl start "${PACKAGE_NAME}"

printf "\n"
success "Vord Agent is installed and running!"
printf "\n"
systemctl status "${PACKAGE_NAME}" --no-pager
