// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { Handle } from '@sveltejs/kit';
import { createServerApiClient } from '$lib/api/server';

const MAX_CACHE_SIZE = 10_000;
const SESSION_TTL_MS = 60_000;
const SWEEP_INTERVAL_MS = 5 * 60 * 1000;

const sessionCache = new Map<string, { user: App.Locals['user']; expiresAt: number }>();

// Periodic sweep of expired entries
setInterval(() => {
	const now = Date.now();
	for (const [key, value] of sessionCache) {
		if (value.expiresAt <= now) {
			sessionCache.delete(key);
		}
	}
}, SWEEP_INTERVAL_MS);

function cacheSet(key: string, user: App.Locals['user'], ttlMs: number) {
	if (sessionCache.size >= MAX_CACHE_SIZE) {
		// Evict oldest (first inserted) entry
		const firstKey = sessionCache.keys().next().value;
		if (firstKey) {
			sessionCache.delete(firstKey);
		}
	}
	sessionCache.set(key, { user, expiresAt: Date.now() + ttlMs });
}

export const handle: Handle = async ({ event, resolve }) => {
	const cookie = event.cookies.get('vord_auth');
	event.locals.user = null;

	if (cookie) {
		const tenantCookie = event.cookies.get('vord_tenant');
		const cacheKey = tenantCookie ? `${cookie}:${tenantCookie}` : cookie;
		const cached = sessionCache.get(cacheKey);
		if (cached && cached.expiresAt > Date.now()) {
			event.locals.user = cached.user;
		} else {
			try {
				const client = createServerApiClient(event.fetch, cookie, tenantCookie);
				const user = await client.getMe();
				event.locals.user = user;
				cacheSet(cacheKey, user, SESSION_TTL_MS);

				// Auto-set vord_tenant cookie if missing but user has a tenant
				if (!tenantCookie && user.activeTenantId) {
					event.cookies.set('vord_tenant', String(user.activeTenantId), {
						path: '/',
						httpOnly: true,
						sameSite: 'lax',
						secure: true
					});
				}
			} catch {
				event.locals.user = null;
				sessionCache.delete(cacheKey);
			}
		}
	}

	return resolve(event, {
		transformPageChunk: ({ html }) => {
			const themeCookie = event.cookies.get('framlux_theme');
			const themeClass = themeCookie === 'dark' ? 'dark' : 'light';
			return html.replace('%framlux.theme%', themeClass);
		}
	});
};
