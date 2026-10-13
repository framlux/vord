#!/bin/sh
# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Package-manager regression test for the agent's deb and rpm. Run it with:
#   make -C src/agent test-packages
#
# The agent has to be enabled and running after a package upgrade, which is decided by the
# maintainer scripts of both the old and the new package, in an order only the real package
# managers produce. This builds the packages with the pinned nfpm and drives apt-get on Debian and
# dnf on Rocky Linux through install, upgrade, downgrade, removal and reinstall in a fresh
# container per scenario (test_packages_scenarios.sh), then checks that the unit ended up enabled
# and running, or disabled and stopped after removal, or still masked if it was masked.
#
# Three sets of packages are built, all from this checkout and without any download of published
# ones:
#   released  the scripts of RELEASED_TAG, the newest agent release whose pre-remove disables the
#             unit, so the upgrade path from a version already out in the field is covered: its
#             old scripts still run on every upgrade, whatever the new package does. It stays on
#             that release rather than following the latest one.
#   current   the working tree
#   next      the working tree again, one version higher, for a new-to-new upgrade
#
# Needs Docker or Podman (CONTAINER_CLI picks one; Docker is preferred), network access to pull
# the base images and, unless NFPM points at a binary, to install nfpm, plus Go, envsubst and a
# checkout that has the RELEASED_TAG tag.

set -eu

RELEASED_TAG="agent-v3.3.0"
CURRENT_VERSION="4.0.0"
NEXT_VERSION="4.0.1"
BASE_IMAGES="debian:12 rockylinux:9"

here="$(cd "$(dirname "$0")" && pwd)"
agent_dir="$(cd "${here}/.." && pwd)"

die() {
    echo "ERROR: $1" >&2
    exit 1
}

if [ -z "${CONTAINER_CLI:-}" ]; then
    if command -v docker >/dev/null 2>&1; then
        CONTAINER_CLI="docker"
    elif command -v podman >/dev/null 2>&1; then
        CONTAINER_CLI="podman"
    else
        die "neither docker nor podman found; set CONTAINER_CLI"
    fi
fi
command -v envsubst >/dev/null 2>&1 || die "envsubst is required (it is part of gettext)"
git -C "${agent_dir}" rev-parse -q --verify "refs/tags/${RELEASED_TAG}^{commit}" >/dev/null ||
    die "tag ${RELEASED_TAG} is missing; fetch tags (git fetch --tags)"

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

NFPM="${NFPM:-}"
if [ -z "${NFPM}" ]; then
    echo "== Installing the pinned nfpm =="
    GOBIN="${work}/bin" sh "${here}/install_nfpm.sh"
    NFPM="${work}/bin/nfpm"
fi

# --- Build the packages ---

# The test only reads the scripts, so the binary is a placeholder and the packages are
# architecture-independent; neither changes what the maintainer scripts do.
export PKG_ARCH="all"
export BIN_ARCH="amd64"
export RPM_SIGN_KEY_FILE=""

# build_packages <tree> <version> <output name>
build_packages() {
    (
        cd "$1"
        mkdir -p publish
        printf '#!/bin/sh\nexit 0\n' > "publish/vord-agent-linux-${BIN_ARCH}"
        chmod 0755 "publish/vord-agent-linux-${BIN_ARCH}"
        VERSION="$2" envsubst < nfpm.yaml > nfpm.resolved.yaml
        "${NFPM}" pkg --config nfpm.resolved.yaml --packager deb --target "${work}/pkgs/$3.deb"
        "${NFPM}" pkg --config nfpm.resolved.yaml --packager rpm --target "${work}/pkgs/$3.rpm"
    )
}

echo "== Building packages =="
mkdir -p "${work}/pkgs" "${work}/released" "${work}/current" "${work}/logs"

repo_dir="$(git -C "${agent_dir}" rev-parse --show-toplevel)"
git -C "${repo_dir}" archive "${RELEASED_TAG}" src/agent/nfpm.yaml src/agent/packaging src/agent/vord-agent.service |
    tar -x -C "${work}/released" --strip-components=2
cp -R "${agent_dir}/nfpm.yaml" "${agent_dir}/packaging" "${agent_dir}/vord-agent.service" "${work}/current/"

build_packages "${work}/released" "${RELEASED_TAG#agent-v}" released
build_packages "${work}/current" "${CURRENT_VERSION}" current
build_packages "${work}/current" "${NEXT_VERSION}" next

# --- Run the scenarios ---

scenarios="$(sh "${here}/test_packages_scenarios.sh" list)"
failures=0
summary=""

for image in ${BASE_IMAGES}; do
    tag="vord-agent-pkgtest-$(echo "${image}" | tr ':/' '--')"
    echo "== Building the ${image} test image =="
    "${CONTAINER_CLI}" build -q -f "${here}/test_packages.Dockerfile" --build-arg "BASE_IMAGE=${image}" -t "${tag}" "${here}" >/dev/null

    echo "== ${image} =="
    for scenario in ${scenarios}; do
        log="${work}/logs/${tag}-${scenario}.log"
        status=0
        "${CONTAINER_CLI}" run --rm \
            -v "${work}/pkgs:/pkgs:ro" \
            -v "${here}:/scenarios:ro" \
            "${tag}" sh /scenarios/test_packages_scenarios.sh "${scenario}" > "${log}" 2>&1 || status=$?

        final="$(sed -n 's/^FINAL //p' "${log}")"
        if [ "${status}" -eq 0 ]; then
            result="PASS"
        else
            result="FAIL"
            failures=$((failures + 1))
        fi
        line="$(printf '%-24s %-22s %-22s %s' "${image}" "${scenario}" "${final:-no result}" "${result}")"
        echo "${line}"
        summary="${summary}${line}
"
        if [ "${status}" -ne 0 ]; then
            sed 's/^/    | /' "${log}"
        fi
    done
done

echo
echo "== Summary =="
printf '%-24s %-22s %-22s %s\n' "image" "scenario" "enabled/active" "result"
printf '%s' "${summary}"

if [ "${failures}" -ne 0 ]; then
    echo
    echo "${failures} scenario(s) failed."
    exit 1
fi
echo
echo "All scenarios passed."
