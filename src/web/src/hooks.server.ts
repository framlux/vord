// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { Handle, HandleFetch } from '@sveltejs/kit';
import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
import { API_BASE, createServerApiClient } from '$lib/api/server';

const API_ORIGIN = new URL(API_BASE).origin;

const MAX_CACHE_SIZE = 10_000;
const SESSION_TTL_MS = 60_000;
const SWEEP_INTERVAL_MS = 5 * 60 * 1000;

const sessionCache = new Map<string, { user: App.Locals['user']; csrfCookie: string | undefined; expiresAt: number }>();

// Periodic sweep of expired entries
setInterval(() => {
	const now = Date.now();
	for (const [key, value] of sessionCache) {
		if (value.expiresAt <= now) {
			sessionCache.delete(key);
		}
	}
}, SWEEP_INTERVAL_MS);

function cacheSet(key: string, user: App.Locals['user'], csrfCookie: string | undefined, ttlMs: number) {
	if (sessionCache.size >= MAX_CACHE_SIZE) {
		// Evict oldest (first inserted) entry
		const firstKey = sessionCache.keys().next().value;
		if (firstKey) {
			sessionCache.delete(firstKey);
		}
	}
	sessionCache.set(key, { user, csrfCookie, expiresAt: Date.now() + ttlMs });
}

// Mirror the backend's vord_csrf antiforgery cookie onto the browser. A server-side fetch to the
// backend does not propagate its Set-Cookie, so we re-issue the value captured from /auth/me here.
// Strict SameSite and HttpOnly match the backend's AntiforgeryStartup.ConfigureOptions; the browser
// can present it to the SvelteKit proxy without script access, which is all the double-submit
// scheme requires.
function setCsrfCookie(event: Parameters<Handle>[0]['event'], csrfCookie: string | undefined) {
	if (!csrfCookie) {
		return;
	}
	event.cookies.set('vord_csrf', csrfCookie, {
		path: '/',
		httpOnly: true,
		sameSite: 'strict',
		secure: !dev
	});
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
	event.locals.csrfCookie = undefined;

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
				event.locals.csrfCookie = cached.csrfCookie;
				// Re-issue the paired antiforgery cookie so a browser that lost it (or never
				// received it) is re-armed without forcing a fresh /auth/me round-trip.
				setCsrfCookie(event, cached.csrfCookie);
			} else {
				try {
					const client = createServerApiClient(event.fetch, cookie, tenantCookie);
					const { user, csrfCookie } = await client.getMeBootstrap();
					event.locals.user = user;
					event.locals.csrfCookie = csrfCookie;
					cacheSet(cacheKey, user, csrfCookie, SESSION_TTL_MS);

					// Mirror the antiforgery cookie onto the browser. The backend's Set-Cookie
					// rides the server-to-server fetch and would otherwise be dropped here.
					setCsrfCookie(event, csrfCookie);

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
					event.locals.csrfCookie = undefined;
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
	// upstream Set-Cookie / Content-Type values pass through untouched. The Content-Security-Policy
	// is deliberately not set here: SvelteKit builds it from kit.csp in svelte.config.js because it
	// alone knows the per-response nonce on the inline script that boots each page, and a header
	// written after resolve() would replace that one and stop every page from hydrating.
	response.headers.set('Strict-Transport-Security', 'max-age=63072000; includeSubDomains; preload');
	response.headers.set('X-Content-Type-Options', 'nosniff');
	response.headers.set('Referrer-Policy', 'strict-origin-when-cross-origin');
	response.headers.set('Permissions-Policy', 'camera=(), microphone=(), geolocation=()');
	response.headers.set('X-Frame-Options', 'DENY');

	return response;
};

// The api-server sits behind an SSL-terminating proxy and trusts X-Forwarded-Proto to learn that a
// request was secure; its antiforgery cookie policy refuses to mint or validate tokens for a request
// it believes is plain http. This pod reaches it over plain in-cluster http, so without this hook
// every server-side fetch (the /auth/me bootstrap and every proxied browser mutation) looks insecure
// to it. Tell the backend the scheme the browser actually used. The header is set, never appended,
// so a value from the original request cannot be smuggled through, and it is not hardcoded to https
// because a self-hosted deployment may legitimately run over plain http. Requests to any other
// origin are left exactly as they were.
export const handleFetch: HandleFetch = async ({ event, request, fetch }) => {
	if (new URL(request.url).origin === API_ORIGIN) {
		request.headers.set('x-forwarded-proto', event.url.protocol.replace(/:$/, ''));
	}

	return fetch(request);
};
