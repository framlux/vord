// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import canonicalInstaller from '../../../../../deployment/agent/install.sh?raw';
import { generateInstallScript } from './install-script';

describe('generateInstallScript', () => {
    it('should start with bash shebang', () => {
        const script = generateInstallScript('test-token-123');
        expect(script.startsWith('#!/usr/bin/env bash')).toBe(true);
    });

    it('should append the canonical installer verbatim', () => {
        // The whole point of the prelude design: there is exactly one installer body, so a fix in
        // deployment/agent/install.sh reaches the dashboard without anyone porting it.
        const script = generateInstallScript('token');
        expect(script.endsWith(canonicalInstaller)).toBe(true);
    });

    it('should include PATH normalization for minimal environments', () => {
        const script = generateInstallScript('test-token');
        expect(script).toContain(
            'export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH}"'
        );
    });

    it('should contain the provided token value', () => {
        const script = generateInstallScript('my-secret-token-abc');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN="my-secret-token-abc"');
    });

    it('should contain the provided server address', () => {
        const script = generateInstallScript('token', 'custom.server.dev');
        expect(script).toContain('export VORD_SERVER_ADDRESS="custom.server.dev"');
    });

    it('should use default server address when not provided', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('export VORD_SERVER_ADDRESS="grpc.app.vordfleet.dev"');
    });

    it('should use default port when not provided', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('export VORD_SERVER_PORT=443');
    });

    it('should use custom port when provided', () => {
        const script = generateInstallScript('token', 'grpc.app.vordfleet.dev', 12234);
        expect(script).toContain('export VORD_SERVER_PORT=12234');
    });

    it('should be non-interactive by construction', () => {
        // The installer prompts only when no token is supplied. Exporting one ahead of the body is
        // what selects the non-interactive path, so asserting the export is the real guarantee —
        // the prompt code still exists in the body and is simply never reached.
        const script = generateInstallScript('token');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN=');
        const preludeEnd = script.indexOf('#!/usr/bin/env bash', 1);
        const prelude = script.slice(0, preludeEnd);
        expect(prelude).not.toContain('read -r');
    });

    it('should restart rather than start, so the agent re-reads the written config', () => {
        // The package postinstall starts the agent before any config exists; `start` on an
        // already-running unit is a no-op, which would leave it running on the placeholder.
        const script = generateInstallScript('token');
        expect(script).toContain('systemctl enable');
        expect(script).toContain('systemctl restart "${PACKAGE_NAME}"');
    });

    it('should contain config file write section', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('Writing configuration');
        expect(script).toContain('CONFIG_FILE');
        expect(script).toContain('chmod 0600');
    });

    it('should contain package installation', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('vord-agent');
        expect(script).toContain('apt-get install');
    });

    it('should escape double quotes in token values', () => {
        const script = generateInstallScript('token"with"quotes');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN="token\\"with\\"quotes"');
    });

    it('should escape dollar signs in token values', () => {
        const script = generateInstallScript('token$var');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN="token\\$var"');
    });

    it('should escape backslashes in token values', () => {
        const script = generateInstallScript('token\\slash');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN="token\\\\slash"');
    });

    it('should escape backticks in token values', () => {
        const script = generateInstallScript('token`cmd`end');
        expect(script).toContain('export VORD_REGISTRATION_TOKEN="token\\`cmd\\`end"');
    });

    it('should escape shell-dangerous characters in server address', () => {
        const script = generateInstallScript('token', 'server"$`\\bad');
        expect(script).toContain('export VORD_SERVER_ADDRESS="server\\"\\$\\`\\\\bad"');
    });

    it('should include use_tls setting', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('use_tls = true');
    });

    it('should include generated-by comment', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('Generated by the Vord Fleet dashboard');
    });

    it('should use the signed-by keyring flow for the apt repository', () => {
        const script = generateInstallScript('token');
        expect(script).toContain('KEYRING_PATH="/usr/share/keyrings/framlux-archive-keyring.gpg"');
        expect(script).toContain('deb [signed-by=${KEYRING_PATH}] ${APT_REPO_URL} * *');
    });

    it('should dearmor the signing key non-interactively so re-runs work', () => {
        // `gpg --dearmor -o FILE` refuses to overwrite an existing FILE and, with no tty, dies —
        // which broke every retry of a failed install.
        const script = generateInstallScript('token');
        expect(script).toContain('gpg --batch --yes --dearmor');
    });

    it('should not enable rpm package signature checking, which the published RPMs lack', () => {
        // The RPMs carry no package signature; the repository metadata is signed instead.
        // gpgcheck=1 would fail every dnf/yum install with "package is not signed".
        const script = generateInstallScript('token');
        expect(script).toContain('gpgcheck=0');
        expect(script).toContain('repo_gpgcheck=1');
    });

    it('should not use the removed apt-key command', () => {
        const script = generateInstallScript('token');
        expect(script).not.toContain('apt-key');
    });

    it('should place the token in the prelude, ahead of the installer body', () => {
        const script = generateInstallScript('my-secret-token-abc');
        const tokenIndex = script.indexOf('export VORD_REGISTRATION_TOKEN="my-secret-token-abc"');
        const bodyIndex = script.indexOf('set -euo pipefail');

        expect(tokenIndex).toBeGreaterThan(-1);
        expect(bodyIndex).toBeGreaterThan(-1);
        expect(tokenIndex).toBeLessThan(bodyIndex);
    });
});
