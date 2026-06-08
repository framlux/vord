// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
import { ApiClient } from './client';
import { MockApiClient } from './mock-client';

const API_BASE = env.API_BASE_URL ?? 'http://127.0.0.1:12233';

export function createServerApiClient(skFetch: typeof fetch, cookie: string | undefined, tenantCookie?: string | undefined, baseUrl?: string): ApiClient {
	// When VORD_API_MOCK=true in a dev build the fleet UI renders against
	// in-memory fixtures instead of the real backend. Used to capture marketing
	// screenshots without DB writes. The `dev` import is a compile-time constant
	// from SvelteKit — it's `false` in production builds, so this branch (and
	// the MockApiClient import above) is dead-code-eliminated and the mock
	// module is not bundled into the prod server output.
	if (dev && env.VORD_API_MOCK === 'true') {
		return new MockApiClient() as unknown as ApiClient;
	}

	const cookieParts: string[] = [];
	if (cookie) cookieParts.push(`vord_auth=${cookie}`);
	if (tenantCookie) cookieParts.push(`vord_tenant=${tenantCookie}`);
	const cookieHeader = cookieParts.join('; ');

	return new ApiClient(baseUrl ?? API_BASE, (input, init) => {
		return skFetch(input, {
			...init,
			headers: {
				...Object.fromEntries(
					Object.entries(init?.headers ?? {}).filter(([, v]) => v !== undefined)
				),
				...(cookieHeader ? { Cookie: cookieHeader } : {})
			}
		});
	});
}
