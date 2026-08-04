// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

/** Where the canonical installer is served from when nothing overrides it. */
const DEFAULT_INSTALL_URL = 'https://get.vordfleet.dev';

/** The agent's own default, so a matching value adds no flag. */
const DEFAULT_SERVER_ADDRESS = 'grpc.app.vordfleet.dev';

/** The agent's own default port. */
const DEFAULT_SERVER_PORT = 443;

/**
 * Escapes a value for safe inclusion inside a single-quoted shell string.
 * Single quotes are the strongest shell quoting available: nothing is interpreted inside them, so
 * the only character needing care is the closing quote itself.
 */
function shellQuote(value: string): string {
    return `'${value.replaceAll("'", `'\\''`)}'`;
}

/**
 * Builds the one-line install command shown in the dashboard, with the machine's registration
 * token filled in.
 *
 * This used to emit the entire ~450-line installer: the canonical script was imported and appended
 * to a prelude of environment variables. That required a copy of the script to live inside the web
 * project (the container is built with `context: src/web`, so an import reaching outside it is
 * absent during the Docker build and failed the release), plus a CI guard to keep the copy honest.
 *
 * None of that was necessary. The installer already accepts `--token`, `--server` and `--port`, and
 * the one-liner below is the exact command documented in the installer's own header, the support
 * articles and the marketing site. Emitting it means there is one installer, served from one place,
 * and the dashboard only has to say how to run it.
 *
 * @param token The machine registration token.
 * @param serverAddress The gRPC server hostname; omitted from the command when it matches the agent default.
 * @param serverPort The gRPC server port; omitted from the command when it matches the agent default.
 * @param installUrl Where the installer is served from; defaults to the hosted URL.
 * @returns A single shell command the operator can paste onto a target machine.
 */
export function generateInstallCommand(
    token: string,
    serverAddress: string = DEFAULT_SERVER_ADDRESS,
    serverPort: number = DEFAULT_SERVER_PORT,
    installUrl: string = DEFAULT_INSTALL_URL
): string {
    // Every value is passed explicitly rather than relying on the agent's compiled-in defaults.
    // Omitting a flag "because it matches the default" would couple this file to a constant in the
    // Go agent: if either side changed, the dashboard would hand out a command that silently
    // connected somewhere else. Being explicit also lets the operator read the command and see
    // exactly where the machine will register.
    const flags: string[] = [
        '--token',
        shellQuote(token),
        '--server',
        shellQuote(serverAddress),
        '--port',
        String(serverPort)
    ];

    return `curl -fsSL ${installUrl} | sudo bash -s -- ${flags.join(' ')}`;
}
