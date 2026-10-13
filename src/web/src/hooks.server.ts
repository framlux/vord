// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { Handle, HandleFetch } from '@sveltejs/kit';
import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
import { API_BASE, createServerApiClient } from '$lib/api/server';

const API_ORIGIN = new URL(API_BASE).origin;

const MAX_CACHE_SIZE = 10_000;
const MAX_PAIRINGS_PER_SESSION = 8;
const SESSION_TTL_MS = 60_000;
const SWEEP_INTERVAL_MS = 5 * 60 * 1000;

const CSRF_COOKIE = 'vord_csrf';

// The framework serialises the antiforgery cookie with base64url (the URL-safe alphabet, no padding),
// so anything else in that cookie is not something it issued.
const CSRF_COOKIE_FORMAT = /^[A-Za-z0-9_-]+$/;

type SessionEntry = { user: App.Locals['user']; csrfCookie: string | undefined; expiresAt: number };

// Two levels: the outer key is the session (vord_auth, plus vord_tenant when present) and the inner key
// is the antiforgery cookie value the entry's token pairs with, or '' when no cookie is paired. An
// entry is only ever served to a browser that holds that exact cookie, because the token it carries
// validates against no other. The inner key is the cookie the backend vouched for (the one it reused
// or the one it just issued), never a value the browser merely sent, so garbage cannot become a key.
const sessionCache = new Map<string, Map<string, SessionEntry>>();

// Periodic sweep of expired entries
setInterval(() => {
	const now = Date.now();
	for (const [sessionKey, pairings] of sessionCache) {
		for (const [pairedCookie, entry] of pairings) {
			if (entry.expiresAt <= now) {
				pairings.delete(pairedCookie);
			}
		}
		if (pairings.size === 0) {
			sessionCache.delete(sessionKey);
		}
	}
}, SWEEP_INTERVAL_MS);

// The cookie value the browser sent, or undefined when it holds none or one the backend could never
// have issued. A value outside the antiforgery cookie's alphabet is not forwarded, because SvelteKit
// hands the hook the decoded value and a decoded line break would make the server-side fetch throw,
// signing the user out of every page. It is also not trusted as a cache key: treated as absent, the
// backend mints a replacement and the hook writes it over the bad cookie.
function browserCsrfCookieOf(event: Parameters<Handle>[0]['event']): string | undefined {
	const value = event.cookies.get(CSRF_COOKIE);

	return value !== undefined && CSRF_COOKIE_FORMAT.test(value) ? value : undefined;
}

function sessionKeyFor(authCookie: string, tenantCookie: string | undefined): string {
	return tenantCookie ? `${authCookie}\0${tenantCookie}` : authCookie;
}

function cacheGet(sessionKey: string, browserCsrfCookie: string | undefined): SessionEntry | undefined {
	const entry = sessionCache.get(sessionKey)?.get(browserCsrfCookie ?? '');
	if (entry && entry.expiresAt > Date.now()) {
		return entry;
	}

	return undefined;
}

function cacheSet(sessionKey: string, user: App.Locals['user'], csrfCookie: string | undefined, ttlMs: number) {
	let pairings = sessionCache.get(sessionKey);
	if (!pairings) {
		if (sessionCache.size >= MAX_CACHE_SIZE) {
			// Evict oldest (first inserted) session
			const firstKey = sessionCache.keys().next().value;
			if (firstKey !== undefined) {
				sessionCache.delete(firstKey);
			}
		}
		pairings = new Map();
		sessionCache.set(sessionKey, pairings);
	}

	const pairedCookie = csrfCookie ?? '';
	pairings.delete(pairedCookie);
	if (pairings.size >= MAX_PAIRINGS_PER_SESSION) {
		// A browser that keeps discarding its cookie must not grow one session without bound.
		const oldestPairing = pairings.keys().next().value;
		if (oldestPairing !== undefined) {
			pairings.delete(oldestPairing);
		}
	}
	pairings.set(pairedCookie, { user, csrfCookie, expiresAt: Date.now() + ttlMs });
}

// Mirror the backend's vord_csrf antiforgery cookie onto the browser. A server-side fetch to the
// backend does not propagate its Set-Cookie, so we re-issue the value captured from /auth/me here.
// Strict SameSite and HttpOnly match the backend's AntiforgeryStartup.ConfigureOptions; the browser
// can present it to the SvelteKit proxy without script access, which is all the double-submit
// scheme requires.
function setCsrfCookie(event: Parameters<Handle>[0]['event'], csrfCookie: string) {
	event.cookies.set(CSRF_COOKIE, csrfCookie, {
		path: '/',
		httpOnly: true,
		sameSite: 'strict',
		secure: !dev
	});
}

// SvelteKit appends the cookies written through event.cookies after the route's own response headers.
// When the route (the browser API proxy relaying a backend response that minted its own antiforgery
// cookie) already carries a vord_csrf Set-Cookie, that one pairs with the token in the same body, so
// ours is dropped and the browser is never handed two competing cookies.
function dropDuplicateCsrfCookie(response: Response) {
	const setCookies = response.headers.getSetCookie();
	const csrfIndexes = setCookies.flatMap((entry, index) => (entry.startsWith(`${CSRF_COOKIE}=`) ? [index] : []));
	if (csrfIndexes.length < 2) {
		return;
	}

	const ourIndex = csrfIndexes[csrfIndexes.length - 1];
	response.headers.delete('set-cookie');
	setCookies.forEach((entry, index) => {
		if (index !== ourIndex) {
			response.headers.append('set-cookie', entry);
		}
	});
}

export function purgeSession(authCookie: string, tenantCookie?: string): void {
	sessionCache.delete(sessionKeyFor(authCookie, tenantCookie));
	// Also purge the key without tenant in case it exists
	if (tenantCookie) {
		sessionCache.delete(authCookie);
	}
}

export const handle: Handle = async ({ event, resolve }) => {
	event.locals.user = null;
	event.locals.csrfCookie = undefined;
	let wroteCsrfCookie = false;

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
			const sessionKey = sessionKeyFor(cookie, tenantCookie);
			const browserCsrfCookie = browserCsrfCookieOf(event);
			const cached = cacheGet(sessionKey, browserCsrfCookie);
			if (cached) {
				// The browser holds the cookie this entry's token pairs with, so there is nothing to write.
				event.locals.user = cached.user;
				event.locals.csrfCookie = cached.csrfCookie;
			} else {
				try {
					// Hand the backend the cookie the browser already holds: it reuses a valid one and
					// answers with a token for it, and mints a replacement (a Set-Cookie) only when there is
					// none or it cannot be read. The browser therefore keeps one cookie for its whole
					// session, so the token a page captured at load keeps validating.
					const client = createServerApiClient(
						event.fetch,
						cookie,
						tenantCookie,
						undefined,
						browserCsrfCookie ? { cookie: browserCsrfCookie } : undefined
					);
					const { user, csrfCookie: issuedCsrfCookie } = await client.getMeBootstrap();

					// No Set-Cookie means the backend reused the cookie it was given.
					const pairedCsrfCookie = issuedCsrfCookie ?? browserCsrfCookie;
					event.locals.user = user;
					event.locals.csrfCookie = pairedCsrfCookie;
					cacheSet(sessionKey, user, pairedCsrfCookie, SESSION_TTL_MS);

					// Write the cookie only when the browser does not already hold it. The backend's
					// Set-Cookie rides the server-to-server fetch and would otherwise be dropped here.
					if (pairedCsrfCookie !== undefined && pairedCsrfCookie !== browserCsrfCookie) {
						setCsrfCookie(event, pairedCsrfCookie);
						wroteCsrfCookie = true;
					}

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
					sessionCache.delete(sessionKey);
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

	if (wroteCsrfCookie) {
		dropDuplicateCsrfCookie(response);
	}

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
