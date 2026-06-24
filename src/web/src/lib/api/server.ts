// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
import { ApiClient } from './client';
import { MockApiClient } from './mock-client';

const API_BASE = env.API_BASE_URL ?? 'http://127.0.0.1:12233';

/**
 * Builds the double-submit antiforgery payload for {@link createServerApiClient} from the request's
 * cookies and the auth-bootstrap user. The vord_csrf cookie value and the user's csrfToken are
 * minted together by GET /auth/me (mirrored onto the browser by hooks.server.ts), so forwarding the
 * cookie and echoing the token in the X-CSRF-TOKEN header satisfies the backend's JSON CSRF gate on
 * state-changing SSR requests. Safe (GET) calls ignore it.
 */
export function csrfFor(
	cookies: { get(name: string): string | undefined },
	locals: { csrfCookie?: string | undefined; user?: { csrfToken?: string | null } | null }
): { cookie?: string | undefined; token?: string | undefined } {
	return {
		cookie: cookies.get('vord_csrf') ?? locals.csrfCookie,
		token: locals.user?.csrfToken ?? undefined
	};
}

export function createServerApiClient(
	skFetch: typeof fetch,
	cookie: string | undefined,
	tenantCookie?: string | undefined,
	baseUrl?: string,
	csrf?: { cookie?: string | undefined; token?: string | undefined }
): ApiClient {
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
	// Forward the antiforgery cookie so the backend can validate the double-submit token on
	// state-changing requests. The matching request token is injected via setCsrfToken below.
	if (csrf?.cookie) cookieParts.push(`vord_csrf=${csrf.cookie}`);
	const cookieHeader = cookieParts.join('; ');

	return new ApiClient(
		baseUrl ?? API_BASE,
		(input, init) => {
			return skFetch(input, {
				...init,
				headers: {
					...Object.fromEntries(
						Object.entries(init?.headers ?? {}).filter(([, v]) => v !== undefined)
					),
					...(cookieHeader ? { Cookie: cookieHeader } : {})
				}
			});
		},
		csrf?.token
	);
}
