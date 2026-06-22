// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { Handle } from '@sveltejs/kit';
import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
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

export function purgeSession(authCookie: string, tenantCookie?: string): void {
	const cacheKey = tenantCookie ? `${authCookie}\0${tenantCookie}` : authCookie;
	sessionCache.delete(cacheKey);
	// Also purge the key without tenant in case it exists
	if (tenantCookie) {
		sessionCache.delete(authCookie);
	}
}

export const handle: Handle = async ({ event, resolve }) => {
	event.locals.user = null;

	// Mock-mode short-circuit: skip cookie validation, populate locals.user
	// from the MockApiClient's getMe(). Do not touch sessionCache or write any
	// auto-cookies — a non-mock restart should not inherit fake state. The
	// `dev` gate makes this branch dead in production builds.
	if (dev && env.VORD_API_MOCK === 'true') {
		try {
			const client = createServerApiClient(event.fetch, undefined, undefined);
			event.locals.user = await client.getMe();
		} catch {
			event.locals.user = null;
		}
	} else {
		const cookie = event.cookies.get('vord_auth');
		if (cookie) {
			const tenantCookie = event.cookies.get('vord_tenant');
			const cacheKey = tenantCookie ? `${cookie}\0${tenantCookie}` : cookie;
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
							secure: !dev
						});
					}
				} catch {
					event.locals.user = null;
					sessionCache.delete(cacheKey);
				}
			}
		}
	}

	const response = await resolve(event, {
		transformPageChunk: ({ html }) => {
			const themeCookie = event.cookies.get('framlux_theme');
			const themeClass = themeCookie === 'dark' ? 'dark' : 'light';
			return html.replace('%framlux.theme%', themeClass);
		}
	});

	// Security headers — mirror the .NET server's SecurityHeadersMiddleware so the SvelteKit
	// front-door (which the browser hits first) carries the same protections. Existing
	// upstream Set-Cookie / Content-Type values pass through untouched.
	response.headers.set(
		'Content-Security-Policy',
		"default-src 'self'; "
			+ "base-uri 'self'; "
			+ "frame-ancestors 'none'; "
			+ "img-src 'self' data:; "
			+ "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
			+ "font-src 'self' https://fonts.gstatic.com; "
			+ "script-src 'self'; "
			+ "connect-src 'self'; "
			+ "object-src 'none'"
	);
	response.headers.set('Strict-Transport-Security', 'max-age=63072000; includeSubDomains; preload');
	response.headers.set('X-Content-Type-Options', 'nosniff');
	response.headers.set('Referrer-Policy', 'strict-origin-when-cross-origin');
	response.headers.set('Permissions-Policy', 'camera=(), microphone=(), geolocation=()');
	response.headers.set('X-Frame-Options', 'DENY');

	return response;
};
