// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { HandleFetch } from '@sveltejs/kit';

// `dev` drives whether auto-set cookies are marked Secure. Default the test
// suite to a dev build so the non-mock branch in `handle` runs without the
// mock short-circuit; individual cases override env as needed.
vi.mock('$app/environment', () => ({ dev: true }));
vi.mock('$env/dynamic/private', () => ({ env: { VORD_API_MOCK: 'false' } }));

const getMeBootstrapMock = vi.fn();
vi.mock('$lib/api/server', () => ({
    API_BASE: 'http://backend:12233',
    createServerApiClient: () => ({ getMeBootstrap: getMeBootstrapMock })
}));

import { handle, handleFetch } from './hooks.server';

type CookieSet = { name: string; value: string; opts: Record<string, unknown> };

function makeEvent(cookies: Record<string, string>, sets: CookieSet[]) {
    return {
        locals: {} as App.Locals,
        fetch: vi.fn(),
        cookies: {
            get: (name: string) => cookies[name],
            set: (name: string, value: string, opts: Record<string, unknown>) => {
                sets.push({ name, value, opts });
            }
        }
    } as unknown as Parameters<typeof handle>[0]['event'];
}

const resolve = vi.fn(async () => new Response('<html></html>'));

describe('hooks.server handle — vord_tenant auto-set cookie', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it('auto-sets vord_tenant with secure:false in a dev (non-HTTPS) build', async () => {
        getMeBootstrapMock.mockResolvedValue({ user: { activeTenantId: 7 }, csrfCookie: undefined });

        const sets: CookieSet[] = [];
        const event = makeEvent({ vord_auth: 'token-abc' }, sets);

        await handle({ event, resolve });

        const tenantSet = sets.find((s) => s.name === 'vord_tenant');
        expect(tenantSet).toBeDefined();
        expect(tenantSet?.value).toBe('7');
        // The bug this guards against: hardcoded secure:true breaks local
        // non-HTTPS dev because the browser drops Secure cookies over http://.
        expect(tenantSet?.opts.secure).toBe(false);
        expect(tenantSet?.opts.httpOnly).toBe(true);
        expect(tenantSet?.opts.sameSite).toBe('lax');
        expect(tenantSet?.opts.path).toBe('/');
    });

    it('does not auto-set vord_tenant when one is already present', async () => {
        getMeBootstrapMock.mockResolvedValue({ user: { activeTenantId: 7 }, csrfCookie: undefined });

        const sets: CookieSet[] = [];
        const event = makeEvent({ vord_auth: 'token-abc', vord_tenant: '7' }, sets);

        await handle({ event, resolve });

        expect(sets.find((s) => s.name === 'vord_tenant')).toBeUndefined();
    });

    it('mirrors the vord_csrf antiforgery cookie onto the browser', async () => {
        getMeBootstrapMock.mockResolvedValue({
            user: { activeTenantId: 7 },
            csrfCookie: 'csrf-cookie-value'
        });

        const sets: CookieSet[] = [];
        // Unique auth token so the module-level session cache (which persists across tests in
        // this suite) does not serve a stale, csrf-less entry from an earlier case.
        const event = makeEvent({ vord_auth: 'token-csrf-mirror', vord_tenant: '7' }, sets);

        await handle({ event, resolve });

        const csrfSet = sets.find((s) => s.name === 'vord_csrf');
        expect(csrfSet).toBeDefined();
        expect(csrfSet?.value).toBe('csrf-cookie-value');
        expect(csrfSet?.opts.httpOnly).toBe(true);
        expect(csrfSet?.opts.sameSite).toBe('strict');
        expect(csrfSet?.opts.secure).toBe(false);
        expect(csrfSet?.opts.path).toBe('/');
    });

    it('does not set vord_csrf when the backend issued no antiforgery cookie', async () => {
        getMeBootstrapMock.mockResolvedValue({ user: { activeTenantId: 7 }, csrfCookie: undefined });

        const sets: CookieSet[] = [];
        const event = makeEvent({ vord_auth: 'token-csrf-absent', vord_tenant: '7' }, sets);

        await handle({ event, resolve });

        expect(sets.find((s) => s.name === 'vord_csrf')).toBeUndefined();
    });
});

// The api-server rejects a state-changing or antiforgery-minting request that does not arrive over
// TLS, and in-cluster it is reached over plain http, so handleFetch tells it which scheme the
// browser actually used. These cases pin that header to the browser's scheme and to the backend only.
describe('hooks.server handleFetch — x-forwarded-proto for the backend', () => {
    async function forwardedRequest(eventUrl: string, request: Request): Promise<Request> {
        const downstream = vi.fn<typeof fetch>(async () => new Response('{}'));
        const event = { url: new URL(eventUrl) } as unknown as Parameters<HandleFetch>[0]['event'];

        await handleFetch({ event, request, fetch: downstream });

        return downstream.mock.calls[0][0] as Request;
    }

    it('tells the backend the browser used https when the incoming request was https', async () => {
        const forwarded = await forwardedRequest(
            'https://app.vordfleet.dev/dashboard',
            new Request('http://backend:12233/api/v1/auth/me')
        );

        expect(forwarded.headers.get('x-forwarded-proto')).toBe('https');
    });

    it('tells the backend the browser used http when the incoming request was plain http', async () => {
        const forwarded = await forwardedRequest(
            'http://localhost:5173/dashboard',
            new Request('http://backend:12233/api/v1/auth/me')
        );

        expect(forwarded.headers.get('x-forwarded-proto')).toBe('http');
    });

    it('overwrites a pre-existing x-forwarded-proto instead of appending to it', async () => {
        const forwarded = await forwardedRequest(
            'http://localhost:5173/dashboard',
            new Request('http://backend:12233/api/v1/auth/me', {
                headers: { 'x-forwarded-proto': 'https' }
            })
        );

        // An appended value would read "https, http"; the backend must see exactly the browser's scheme.
        expect(forwarded.headers.get('x-forwarded-proto')).toBe('http');
    });

    it.each([
        ['another internal service', 'https://billing.vordfleet.dev/v1/checkout'],
        ['an external host', 'https://api.github.com/user'],
        ['the backend host on a different port', 'http://backend:9999/api/v1/auth/me'],
        ['a host that merely starts with the backend host', 'http://backend.evil.example:12233/api/v1/auth/me']
    ])('does not add x-forwarded-proto to a request for %s', async (_label, url) => {
        const request = new Request(url);

        const forwarded = await forwardedRequest('https://app.vordfleet.dev/dashboard', request);

        expect(forwarded.headers.has('x-forwarded-proto')).toBe(false);
        expect(forwarded.url).toBe(request.url);
    });

    it('preserves the method, body and other headers of the backend request', async () => {
        const forwarded = await forwardedRequest(
            'https://app.vordfleet.dev/machines',
            new Request('http://backend:12233/api/v1/machines/7', {
                method: 'PUT',
                headers: {
                    'content-type': 'application/json',
                    cookie: 'vord_auth=token; vord_tenant=7',
                    'x-csrf-token': 'csrf-token-abc'
                },
                body: JSON.stringify({ name: 'web-01' })
            })
        );

        expect(forwarded.method).toBe('PUT');
        expect(forwarded.url).toBe('http://backend:12233/api/v1/machines/7');
        expect(forwarded.headers.get('content-type')).toBe('application/json');
        expect(forwarded.headers.get('cookie')).toBe('vord_auth=token; vord_tenant=7');
        expect(forwarded.headers.get('x-csrf-token')).toBe('csrf-token-abc');
        expect(await forwarded.text()).toBe('{"name":"web-01"}');
    });

    it('returns the response the backend produced', async () => {
        const upstream = new Response('{"ok":true}', { status: 201 });
        const downstream = vi.fn<typeof fetch>(async () => upstream);
        const event = { url: new URL('https://app.vordfleet.dev/') } as unknown as Parameters<HandleFetch>[0]['event'];

        const response = await handleFetch({
            event,
            request: new Request('http://backend:12233/api/v1/auth/me'),
            fetch: downstream
        });

        expect(response).toBe(upstream);
    });
});
