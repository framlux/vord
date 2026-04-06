#!/usr/bin/env bash
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Vord Agent installer — automates repo setup, package install, and configuration.
# Usage: curl -fsSL https://get.vordfleet.dev/install.sh | sudo bash

set -euo pipefail

GPG_KEY_URL="https://apt.fury.io/framlux/gpg.key"
APT_REPO_URL="https://packages.framlux.io/apt/"
YUM_REPO_URL="https://packages.framlux.io/yum/"
PACKAGE_NAME="vord-agent"
CONFIG_DIR="/etc/framlux"
CONFIG_FILE="${CONFIG_DIR}/vord-agent.toml"
DEFAULT_SERVER="grpc.vordfleet.dev"
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

# --- Import GPG Key ---

info "Importing Framlux GPG key..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    if command -v gpg >/dev/null 2>&1; then
        curl -fsSL "${GPG_KEY_URL}" | gpg --dearmor -o /usr/share/keyrings/framlux-archive-keyring.gpg
    else
        error "gpg is required but not installed. Install it with: apt-get install gnupg"
        exit 1
    fi
else
    rpm --import "${GPG_KEY_URL}"
fi

# --- Add Package Repository ---

info "Adding Framlux package repository..."

if [ "${PKG_MANAGER}" = "apt" ]; then
    cat > /etc/apt/sources.list.d/framlux.list <<EOF
deb [signed-by=/usr/share/keyrings/framlux-archive-keyring.gpg] ${APT_REPO_URL} * *
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

# --- Interactive Prompts ---

printf "\n"
printf "Enter the server address [%s]: " "${DEFAULT_SERVER}"
read -r SERVER_ADDRESS
if [ -z "${SERVER_ADDRESS}" ]; then
    SERVER_ADDRESS="${DEFAULT_SERVER}"
fi

printf "Enter your registration token: "
read -r REGISTRATION_TOKEN
if [ -z "${REGISTRATION_TOKEN}" ]; then
    error "Registration token is required. You can find it in the Vord Fleet dashboard under Machines > Register."
    exit 1
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

# --- Enable and Start the Agent ---

info "Enabling and starting ${PACKAGE_NAME}..."

systemctl enable "${PACKAGE_NAME}"
systemctl start "${PACKAGE_NAME}"

printf "\n"
success "Vord Agent is installed and running!"
printf "\n"
systemctl status "${PACKAGE_NAME}" --no-pager
