#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Installs the pinned nfpm that builds the agent's deb and rpm packages. The release workflow and
# the package regression test both call this, so the packages that are tested are built by the same
# nfpm that builds the ones that ship. Bump the version here and nowhere else. nfpm lands in
# $GOBIN, or in $(go env GOPATH)/bin when GOBIN is unset.

set -eu

NFPM_VERSION="v2.41.3"

go install "github.com/goreleaser/nfpm/v2/cmd/nfpm@${NFPM_VERSION}"
