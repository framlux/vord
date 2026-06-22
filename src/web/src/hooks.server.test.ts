// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// `dev` drives whether auto-set cookies are marked Secure. Default the test
// suite to a dev build so the non-mock branch in `handle` runs without the
// mock short-circuit; individual cases override env as needed.
vi.mock('$app/environment', () => ({ dev: true }));
vi.mock('$env/dynamic/private', () => ({ env: { VORD_API_MOCK: 'false' } }));

const getMeMock = vi.fn();
vi.mock('$lib/api/server', () => ({
    createServerApiClient: () => ({ getMe: getMeMock })
}));

import { handle } from './hooks.server';

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
        getMeMock.mockResolvedValue({ activeTenantId: 7 } as App.Locals['user']);

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
        getMeMock.mockResolvedValue({ activeTenantId: 7 } as App.Locals['user']);

        const sets: CookieSet[] = [];
        const event = makeEvent({ vord_auth: 'token-abc', vord_tenant: '7' }, sets);

        await handle({ event, resolve });

        expect(sets.find((s) => s.name === 'vord_tenant')).toBeUndefined();
    });
});
