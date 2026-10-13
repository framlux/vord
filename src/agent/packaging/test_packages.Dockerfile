# Copyright (c) 2026 Framlux LLC
# Licensed under the MIT License
# See LICENSE for details.
#
# Base image for the package tests (test_packages.sh): the distro's own package manager plus
# systemd's tools, with test_systemctl_shim.sh installed in front of the real systemctl.

ARG BASE_IMAGE
FROM ${BASE_IMAGE}

RUN if command -v apt-get >/dev/null 2>&1; then \
        apt-get update -qq \
        && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends systemd \
        && rm -rf /var/lib/apt/lists/*; \
    else \
        dnf install -y -q systemd && dnf clean all; \
    fi

COPY test_systemctl_shim.sh /tmp/systemctl-shim
RUN mv /usr/bin/systemctl /usr/bin/systemctl.real \
    && install -m 0755 /tmp/systemctl-shim /usr/bin/systemctl \
    && rm /tmp/systemctl-shim
