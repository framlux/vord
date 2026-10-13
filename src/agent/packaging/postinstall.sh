#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.

set -e

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

# What happens to the unit depends on the operation, which $1 names. An upgrade enables the unit
# again because the outgoing package's pre-remove has already run by now, and every release before
# this fix disabled the unit there; an operator who wants the agent off masks it, which makes the
# enable and the start below fail harmlessly.
#   deb: "configure", with the previously configured version in $2 when there was one (an upgrade,
#        a downgrade, or a reinstall after a removal), then the "abort-*" forms dpkg uses to undo a
#        failed removal or upgrade.
#   rpm: the number of copies installed, which says nothing about whether the service needs
#        starting; posttrans.sh does that once the whole transaction, including the old package's
#        pre-remove, is behind it.
case "${1:-}" in
    configure)
        systemctl daemon-reload || true
        systemctl enable vord-agent || true
        if [ -z "${2:-}" ]; then
            systemctl start vord-agent || true
        else
            systemctl restart vord-agent || true
        fi
        ;;
    abort-remove | abort-deconfigure)
        systemctl enable vord-agent || true
        systemctl start vord-agent || true
        ;;
    abort-upgrade)
        systemctl start vord-agent || true
        ;;
    [0-9]*)
        systemctl daemon-reload || true
        ;;
    *)
        ;;
esac
