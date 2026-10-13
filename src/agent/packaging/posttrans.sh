#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.

set -e

# rpm only (nfpm's rpm.scripts.posttrans). rpm runs this once, after the whole transaction: on an
# upgrade that is after the old package's pre-remove and post-remove, which run after the new
# package's post-install. Every release before this fix stopped and disabled the unit in its
# pre-remove and those old scripts always run on an upgrade, so this is the one place that can
# leave the agent enabled and running.
#
# The argument is deliberately ignored. rpm passes the number of copies installed, and different
# rpm versions have been seen to pass 1 and 0 for the same upgrade, so it cannot tell a fresh
# install from an upgrade. It does not need to: enabling is idempotent and restart starts a unit
# that is not running. A masked unit makes both fail, which is the operator's opt-out.
systemctl daemon-reload || true
systemctl enable vord-agent || true
systemctl restart vord-agent || true
