// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { env } from '$env/dynamic/private';
import { ApiClient } from './client';

const API_BASE = env.API_BASE_URL ?? 'http://127.0.0.1:12233';

export function createServerApiClient(skFetch: typeof fetch, cookie: string | undefined, tenantCookie?: string | undefined, baseUrl?: string): ApiClient {
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
