// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('$app/environment', () => ({ dev: true }));
vi.mock('$env/dynamic/private', () => ({ env: { VORD_API_MOCK: 'false' } }));

type Hooks = typeof import('./hooks.server');
type Handle = Hooks['handle'];
type CookieSet = { name: string; value: string; opts: Record<string, unknown> };

const T0 = new Date('2026-03-01T12:00:00Z');
const CSRF = 'vord_csrf';

// A stand-in for the api-server's /auth/me with the framework's antiforgery behaviour: a request
// that presents a cookie it issued is answered with a token for that cookie and no Set-Cookie, and
// anything else (no cookie, or one it cannot read) gets a newly minted cookie in a Set-Cookie header.
// One instance serves every simulated web replica, as the real key ring is shared between them.
class FakeBackend {
	readonly valid = new Set<string>();
	readonly requests: { cookieHeader: string }[] = [];
	private minted = 0;

	get meCalls(): number {
		return this.requests.length;
	}

	readonly fetch = async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
		// Building real Headers makes this stub refuse what a real fetch refuses, such as a line break
		// inside a header value.
		const cookieHeader = new Headers(init?.headers).get('cookie') ?? '';
		this.requests.push({ cookieHeader });

		const sent = cookieHeader
			.split('; ')
			.find((part) => part.startsWith(`${CSRF}=`))
			?.slice(CSRF.length + 1);

		const headers = new Headers({ 'content-type': 'application/json' });
		let cookie: string;
		if (sent !== undefined && this.valid.has(sent)) {
			cookie = sent;
		} else {
			cookie = `issued-${++this.minted}`;
			this.valid.add(cookie);
			headers.append('set-cookie', `${CSRF}=${cookie}; path=/; samesite=strict; httponly`);
		}

		const user = {
			id: 1,
			name: 'Test User',
			email: 'test@example.com',
			avatar: '',
			isGlobalAdmin: false,
			uniqueId: 'abc-123',
			needsOnboarding: false,
			tenants: [],
			activeTenantId: 7,
			csrfToken: `token-for-${cookie}`
		};

		return new Response(JSON.stringify({ success: true, data: user, message: null, errors: null }), {
			status: 200,
			headers
		});
	};
}

// A browser's cookie jar. Requests are made from it, and the Set-Cookie headers on each response are
// applied back to it, so a test observes what the browser would really end up holding.
class Browser {
	readonly jar = new Map<string, string>();
	readonly csrfHistory: (string | undefined)[] = [];

	constructor(initial: Record<string, string> = {}) {
		for (const [name, value] of Object.entries(initial)) {
			this.jar.set(name, value);
		}
	}

	async visit(
		handle: Handle,
		backend: FakeBackend,
		upstream: () => Response = () => new Response('<html></html>')
	) {
		const sets: CookieSet[] = [];
		const event = {
			locals: {} as App.Locals,
			fetch: backend.fetch,
			// SvelteKit answers a read from a cookie set earlier in the same request with the new value.
			cookies: {
				get: (name: string) => sets.findLast((s) => s.name === name)?.value ?? this.jar.get(name),
				set: (name: string, value: string, opts: Record<string, unknown>) => {
					sets.push({ name, value, opts });
				}
			}
		} as unknown as Parameters<Handle>[0]['event'];

		// SvelteKit appends the cookies set through event.cookies to the response inside resolve(),
		// after whatever headers the route's own response already carries.
		const resolve = async () => {
			const response = upstream();
			for (const s of sets) {
				response.headers.append('set-cookie', `${s.name}=${s.value}; Path=/`);
			}

			return response;
		};

		const response = await handle({ event, resolve });

		for (const entry of response.headers.getSetCookie()) {
			const [pair] = entry.split(';');
			const eq = pair.indexOf('=');
			this.jar.set(pair.slice(0, eq), pair.slice(eq + 1));
		}
		this.csrfHistory.push(this.jar.get(CSRF));

		return {
			event,
			response,
			sets,
			csrfSets: sets.filter((s) => s.name === CSRF),
			csrfSetCookies: response.headers.getSetCookie().filter((c) => c.startsWith(`${CSRF}=`))
		};
	}
}

// The module starts a sweep interval when it loads, so the clock is faked before every import and
// each import is a fresh module with its own session cache: one simulated web replica.
async function startReplica(): Promise<Hooks> {
	vi.resetModules();

	return await import('./hooks.server');
}

describe('hooks.server handle — antiforgery cookie stability', () => {
	let backend: FakeBackend;

	beforeEach(() => {
		vi.useFakeTimers();
		vi.setSystemTime(T0);
		backend = new FakeBackend();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('forwards the cookie the browser holds when it asks the backend who the user is', async () => {
		const { handle } = await startReplica();
		backend.valid.add('cookie-a');
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });

		await browser.visit(handle, backend);

		expect(backend.requests).toHaveLength(1);
		expect(backend.requests[0].cookieHeader).toBe('vord_auth=auth-1; vord_tenant=7; vord_csrf=cookie-a');
	});

	it('sends no antiforgery cookie to the backend when the browser holds none', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		await browser.visit(handle, backend);

		expect(backend.requests[0].cookieHeader).toBe('vord_auth=auth-1; vord_tenant=7');
	});

	it('leaves the browser cookie alone when the backend reuses it', async () => {
		const { handle } = await startReplica();
		backend.valid.add('cookie-a');
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });

		const visit = await browser.visit(handle, backend);

		expect(visit.csrfSets).toEqual([]);
		expect(visit.csrfSetCookies).toEqual([]);
		expect(browser.jar.get(CSRF)).toBe('cookie-a');
		expect(visit.event.locals.csrfCookie).toBe('cookie-a');
		expect(visit.event.locals.user?.csrfToken).toBe('token-for-cookie-a');
	});

	it('writes the issued cookie to a browser that has none', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		const visit = await browser.visit(handle, backend);

		expect(visit.csrfSets).toHaveLength(1);
		expect(visit.csrfSets[0].value).toBe('issued-1');
		expect(visit.csrfSets[0].opts).toMatchObject({ path: '/', httpOnly: true, sameSite: 'strict', secure: false });
		expect(browser.jar.get(CSRF)).toBe('issued-1');
		expect(visit.event.locals.csrfCookie).toBe('issued-1');
		expect(visit.event.locals.user?.csrfToken).toBe('token-for-issued-1');
	});

	it('replaces a cookie the backend cannot read and never keys the cache by it', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'garbage' });

		const first = await browser.visit(handle, backend);

		expect(first.csrfSets.map((s) => s.value)).toEqual(['issued-1']);
		expect(first.event.locals.csrfCookie).toBe('issued-1');
		expect(first.event.locals.user?.csrfToken).toBe('token-for-issued-1');
		expect(browser.jar.get(CSRF)).toBe('issued-1');

		// A browser that still sends the garbage value (it dropped the Set-Cookie) must not be served
		// the entry that was stored for the cookie issued to the first visit.
		browser.jar.set(CSRF, 'garbage');
		const second = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(second.event.locals.user?.csrfToken).toBe('token-for-issued-2');
		expect(second.event.locals.user?.csrfToken).not.toBe('token-for-garbage');
		expect(second.csrfSets.map((s) => s.value)).toEqual(['issued-2']);
	});

	it('serves the cached session only to a browser holding the cookie it pairs with', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		await browser.visit(handle, backend);
		const hit = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(1);
		expect(hit.event.locals.user?.csrfToken).toBe('token-for-issued-1');
		expect(hit.event.locals.csrfCookie).toBe('issued-1');
	});

	it('does not serve an unpaired token when the browser lost the Set-Cookie', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		await browser.visit(handle, backend);
		browser.jar.delete(CSRF);

		const second = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(second.event.locals.user?.csrfToken).toBe('token-for-issued-2');
		expect(browser.jar.get(CSRF)).toBe('issued-2');
	});

	it('does not serve an unpaired token when the browser holds an older valid cookie', async () => {
		const { handle } = await startReplica();
		backend.valid.add('older');
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		await browser.visit(handle, backend);
		browser.jar.set(CSRF, 'older');

		const second = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(second.event.locals.user?.csrfToken).toBe('token-for-older');
		expect(second.csrfSets).toEqual([]);
		expect(browser.jar.get(CSRF)).toBe('older');
	});

	it('caches two valid cookies of one session separately, each with its own token', async () => {
		const { handle } = await startReplica();
		backend.valid.add('cookie-a');
		backend.valid.add('cookie-b');
		const tabA = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });
		const tabB = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-b' });

		const a1 = await tabA.visit(handle, backend);
		const b1 = await tabB.visit(handle, backend);
		const a2 = await tabA.visit(handle, backend);
		const b2 = await tabB.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(a1.event.locals.user?.csrfToken).toBe('token-for-cookie-a');
		expect(a2.event.locals.user?.csrfToken).toBe('token-for-cookie-a');
		expect(b1.event.locals.user?.csrfToken).toBe('token-for-cookie-b');
		expect(b2.event.locals.user?.csrfToken).toBe('token-for-cookie-b');
		expect(a2.event.locals.csrfCookie).toBe('cookie-a');
		expect(b2.event.locals.csrfCookie).toBe('cookie-b');
	});

	it('never writes the antiforgery cookie on a cache hit', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		const miss = await browser.visit(handle, backend);
		const hit1 = await browser.visit(handle, backend);
		const hit2 = await browser.visit(handle, backend);

		expect(miss.csrfSets).toHaveLength(1);
		expect(backend.meCalls).toBe(1);
		expect(hit1.csrfSets).toEqual([]);
		expect(hit2.csrfSets).toEqual([]);
		expect(hit2.csrfSetCookies).toEqual([]);
	});

	it('asks the backend again once the cached session expires and still keeps the same cookie', async () => {
		const { handle } = await startReplica();
		backend.valid.add('cookie-a');
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });

		await browser.visit(handle, backend);
		vi.advanceTimersByTime(59_000);
		await browser.visit(handle, backend);
		expect(backend.meCalls).toBe(1);

		vi.advanceTimersByTime(2_000);
		const expired = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(expired.csrfSets).toEqual([]);
		expect(browser.csrfHistory).toEqual(['cookie-a', 'cookie-a', 'cookie-a']);
	});

	it('survives the periodic sweep and re-bootstraps entries it removed', async () => {
		const { handle } = await startReplica();
		backend.valid.add('cookie-a');
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });

		await browser.visit(handle, backend);
		vi.advanceTimersByTime(6 * 60 * 1000);
		const after = await browser.visit(handle, backend);

		expect(backend.meCalls).toBe(2);
		expect(after.csrfSets).toEqual([]);
	});

	it('keeps one browser cookie across misses on two replicas', async () => {
		const replica1 = await startReplica();
		const replica2 = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });

		const first = await browser.visit(replica1.handle, backend);
		const onOther = await browser.visit(replica2.handle, backend);
		vi.advanceTimersByTime(61_000);
		const again1 = await browser.visit(replica1.handle, backend);
		const again2 = await browser.visit(replica2.handle, backend);

		expect(first.csrfSets.map((s) => s.value)).toEqual(['issued-1']);
		expect(onOther.csrfSets).toEqual([]);
		expect(again1.csrfSets).toEqual([]);
		expect(again2.csrfSets).toEqual([]);
		expect(backend.meCalls).toBe(4);
		expect(browser.csrfHistory).toEqual(['issued-1', 'issued-1', 'issued-1', 'issued-1']);
		for (const visit of [first, onOther, again1, again2]) {
			expect(visit.event.locals.user?.csrfToken).toBe('token-for-issued-1');
		}
	});

	it('adds no second antiforgery cookie when the response already sets one', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });
		const proxied = () => {
			const response = new Response('{}');
			response.headers.append('set-cookie', `${CSRF}=from-upstream; path=/; samesite=strict; httponly`);

			return response;
		};

		const visit = await browser.visit(handle, backend, proxied);

		expect(visit.csrfSetCookies).toHaveLength(1);
		expect(visit.csrfSetCookies[0]).toContain(`${CSRF}=from-upstream`);
		expect(browser.jar.get(CSRF)).toBe('from-upstream');
	});

	it('still writes the issued cookie when the response sets other cookies only', async () => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7' });
		const other = () => {
			const response = new Response('{}');
			response.headers.append('set-cookie', 'framlux_theme=dark; path=/');

			return response;
		};

		const visit = await browser.visit(handle, backend, other);

		expect(visit.csrfSetCookies).toHaveLength(1);
		expect(browser.jar.get(CSRF)).toBe('issued-1');
		expect(browser.jar.get('framlux_theme')).toBe('dark');
	});
});

describe('hooks.server handle — malformed antiforgery cookie from the browser', () => {
	let backend: FakeBackend;

	beforeEach(() => {
		vi.useFakeTimers();
		vi.setSystemTime(T0);
		backend = new FakeBackend();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('treats a cookie that decodes to a line break as absent and writes a fresh one', async () => {
		const { handle } = await startReplica();
		// The browser sent vord_csrf=a%0Ab; SvelteKit hands the hook the decoded value.
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'a\nb' });

		const visit = await browser.visit(handle, backend);

		expect(visit.event.locals.user).not.toBeNull();
		expect(visit.event.locals.user?.csrfToken).toBe('token-for-issued-1');
		expect(backend.requests).toHaveLength(1);
		expect(backend.requests[0].cookieHeader).toBe('vord_auth=auth-1; vord_tenant=7');
		expect(visit.csrfSets.map((s) => s.value)).toEqual(['issued-1']);
		expect(browser.jar.get(CSRF)).toBe('issued-1');
	});

	it.each([
		['an empty value', ''],
		['a space', 'abc def'],
		['a semicolon', 'abc;def'],
		['a comma', 'abc,def'],
		['padding', 'abcd=='],
		['a carriage return', 'abc\rdef'],
		['a trailing line feed', 'abcdef\n'],
		['a NUL', 'abc\u0000def'],
		['non-ASCII text', 'abc\u00e9def'],
		['the base64 alphabet', 'abc+def/ghi']
	])('does not forward a cookie containing %s and replaces it', async (_label, value) => {
		const { handle } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: value });

		const visit = await browser.visit(handle, backend);

		expect(visit.event.locals.user).not.toBeNull();
		expect(backend.requests[0].cookieHeader).toBe('vord_auth=auth-1; vord_tenant=7');
		expect(visit.csrfSets.map((s) => s.value)).toEqual(['issued-1']);
		expect(browser.jar.get(CSRF)).toBe('issued-1');
	});

	it('forwards every character of the base64url alphabet', async () => {
		const { handle } = await startReplica();
		const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
		backend.valid.add(alphabet);
		const browser = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: alphabet });

		const visit = await browser.visit(handle, backend);

		expect(backend.requests[0].cookieHeader).toBe(`vord_auth=auth-1; vord_tenant=7; vord_csrf=${alphabet}`);
		expect(visit.csrfSets).toEqual([]);
		expect(visit.event.locals.csrfCookie).toBe(alphabet);
	});
});

// A session may not hold an unbounded number of pairings and the process may not hold an unbounded
// number of sessions. Both limits evict the oldest entry first.
describe('hooks.server handle — session cache bounds', () => {
	let backend: FakeBackend;

	beforeEach(() => {
		vi.useFakeTimers();
		vi.setSystemTime(T0);
		backend = new FakeBackend();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('keeps at most eight pairings per session and evicts the oldest', async () => {
		const { handle } = await startReplica();
		const tabs = Array.from({ length: 9 }, (_, i) => {
			backend.valid.add(`cookie-${i + 1}`);

			return new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: `cookie-${i + 1}` });
		});
		for (const tab of tabs) {
			await tab.visit(handle, backend);
		}
		expect(backend.meCalls).toBe(9);

		// The newest pairing and the ones after the oldest are still cached.
		for (const tab of tabs.slice(1)) {
			await tab.visit(handle, backend);
		}
		expect(backend.meCalls).toBe(9);

		// The oldest was evicted to make room for the ninth.
		await tabs[0].visit(handle, backend);
		expect(backend.meCalls).toBe(10);
	});

	it('keeps at most ten thousand sessions and evicts the oldest', async () => {
		const { handle } = await startReplica();
		const sessions = Array.from({ length: 10_001 }, (_, i) => new Browser({ vord_auth: `auth-${i}`, vord_tenant: '7' }));
		for (const session of sessions) {
			await session.visit(handle, backend);
		}
		expect(backend.meCalls).toBe(10_001);

		// The second session survived the eviction that the 10,001st made room with.
		await sessions[1].visit(handle, backend);
		expect(backend.meCalls).toBe(10_001);

		await sessions[0].visit(handle, backend);
		expect(backend.meCalls).toBe(10_002);
	});
});

describe('hooks.server purgeSession', () => {
	let backend: FakeBackend;

	beforeEach(() => {
		vi.useFakeTimers();
		vi.setSystemTime(T0);
		backend = new FakeBackend();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('removes every cached pairing of the session', async () => {
		const { handle, purgeSession } = await startReplica();
		backend.valid.add('cookie-a');
		backend.valid.add('cookie-b');
		const tabA = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-a' });
		const tabB = new Browser({ vord_auth: 'auth-1', vord_tenant: '7', vord_csrf: 'cookie-b' });
		await tabA.visit(handle, backend);
		await tabB.visit(handle, backend);
		await tabA.visit(handle, backend);
		await tabB.visit(handle, backend);
		expect(backend.meCalls).toBe(2);

		purgeSession('auth-1', '7');

		await tabA.visit(handle, backend);
		await tabB.visit(handle, backend);
		expect(backend.meCalls).toBe(4);
	});

	it('also removes the pairings cached before a tenant was chosen', async () => {
		const { handle, purgeSession } = await startReplica();
		const browser = new Browser({ vord_auth: 'auth-1' });
		await browser.visit(handle, backend);
		expect(backend.meCalls).toBe(1);

		// The first visit stored the session without a tenant; the purge for the tenant-scoped key
		// must clear that entry too.
		purgeSession('auth-1', '7');

		const afterPurge = new Browser({ vord_auth: 'auth-1', vord_csrf: browser.jar.get(CSRF) ?? '' });
		await afterPurge.visit(handle, backend);
		expect(backend.meCalls).toBe(2);
	});

	it('leaves the sessions of other users cached', async () => {
		const { handle, purgeSession } = await startReplica();
		const alice = new Browser({ vord_auth: 'auth-alice', vord_tenant: '7' });
		const bob = new Browser({ vord_auth: 'auth-bob', vord_tenant: '7' });
		await alice.visit(handle, backend);
		await bob.visit(handle, backend);
		expect(backend.meCalls).toBe(2);

		purgeSession('auth-alice', '7');

		await bob.visit(handle, backend);
		expect(backend.meCalls).toBe(2);
		await alice.visit(handle, backend);
		expect(backend.meCalls).toBe(3);
	});
});
