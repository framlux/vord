// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { generateInstallCommand } from './install-script';

describe('generateInstallCommand', () => {
    it('emits the documented one-liner with the token filled in', () => {
        const command = generateInstallCommand('my-secret-token-abc');

        expect(command).toBe(
            "curl -fsSL https://get.vordfleet.dev | sudo bash -s -- " +
                "--token 'my-secret-token-abc' --server 'grpc.app.vordfleet.dev' --port 443"
        );
    });

    it('is a single line, so it can be pasted into a shell', () => {
        // The dashboard used to hand over the entire installer body; the whole point of this change
        // is that the operator copies one line.
        const command = generateInstallCommand('token');

        expect(command).not.toContain('\n');
    });

    it('always passes --server and --port explicitly', () => {
        // Deliberately not omitted when they match the agent's defaults: doing so would couple this
        // file to a constant in the Go agent, and a change on either side would silently point new
        // machines somewhere else.
        const command = generateInstallCommand('token');

        expect(command).toContain("--server 'grpc.app.vordfleet.dev'");
        expect(command).toContain('--port 443');
    });

    it('carries a custom server address and port through', () => {
        const command = generateInstallCommand('token', 'vord.example.com', 12234);

        expect(command).toContain("--server 'vord.example.com'");
        expect(command).toContain('--port 12234');
    });

    it('honours a custom install URL for self-hosted deployments', () => {
        const command = generateInstallCommand(
            'token',
            'grpc.app.vordfleet.dev',
            443,
            'https://install.example.com'
        );

        expect(command).toContain('curl -fsSL https://install.example.com |');
    });

    it('single-quotes the token so shell metacharacters cannot execute', () => {
        // The token reaches a shell verbatim. Without quoting, a value containing $(...) or a
        // semicolon would run as a command on the operator's machine.
        const command = generateInstallCommand('tok;rm -rf /');

        expect(command).toContain("--token 'tok;rm -rf /'");
    });

    it('escapes a single quote inside the token', () => {
        const command = generateInstallCommand("tok'x");

        expect(command).toContain(`--token 'tok'\\''x'`);
    });

    it('escapes shell-dangerous characters in the server address', () => {
        const command = generateInstallCommand('token', "host';id;'");

        expect(command).toContain(`--server 'host'\\'';id;'\\'''`);
    });
});
