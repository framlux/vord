#!/bin/bash
set -e

systemctl daemon-reload

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
