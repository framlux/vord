#!/usr/bin/env bash
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.

set -e

systemctl daemon-reload || true

# Create default config on fresh install only
mkdir -p /etc/framlux

if [ ! -f /etc/framlux/vord-agent.toml ]; then
    cat > /etc/framlux/vord-agent.toml <<'EOF'
# Vord Agent Configuration
# server_address = "vord.example.com"
# server_port = 12234
# data_dir = "/var/lib/vord-agent"
# log_level = "info"
# use_tls = true
EOF
    chmod 0600 /etc/framlux/vord-agent.toml
fi

# Determine if this is a fresh install or upgrade
# deb: $1 = "configure", $2 = old-version (empty on fresh install)
# rpm: $1 = number of installed instances (1 = fresh, 2+ = upgrade)
is_upgrade=false
if [ "${1:-}" = "configure" ] && [ -n "${2:-}" ]; then
    is_upgrade=true
elif [ "${1:-}" -ge 2 ] 2>/dev/null; then
    is_upgrade=true
fi

if [ "${is_upgrade}" = "true" ]; then
    systemctl restart vord-agent || true
else
    systemctl enable vord-agent || true
    systemctl start vord-agent || true
fi
