#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.

set -e

# The package manager runs the outgoing package's pre-remove on every upgrade, downgrade and
# reinstall as well as on removal. Stopping and disabling the unit then would leave the agent down,
# or not coming back after a reboot, after a routine package update. Only a real removal does that.
#   deb: $1 = "remove" (also while purging). "upgrade", "deconfigure" and "failed-upgrade" mean the
#        package is staying.
#   rpm: $1 = the number of copies left installed afterwards: 0 on erase, 1 or more on an upgrade.
case "${1:-}" in
    remove | 0)
        systemctl stop vord-agent || true
        systemctl disable vord-agent || true
        ;;
    *)
        ;;
esac
