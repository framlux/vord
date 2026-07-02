// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// Force the proxy path (non-mock): dev=false so isMockMode() is false regardless of env.
vi.mock('$app/environment', () => ({ dev: false }));
vi.mock('$env/dynamic/private', () => ({ env: { API_BASE_URL: 'http://backend:12233' } }));
vi.mock('$lib/api/mock-fixtures', () => ({
	mockUser: {},
	mockSubscription: {},
	mockFleetOverview: { summary: {} },
	mockMachineList: [],
	mockMachineById: new Map(),
	mockMachineDetailById: new Map(),
	mockMachineAuthorizedKeys: [],
	mockFleetSshSessions: [],
	mockAlertRules: [],
	getMockMachineAlertRules: () => []
}));

import { GET, PUT } from './+server';

type HeaderPairs = Record<string, string>;

function makeEvent(path: string, headers: HeaderPairs, method = 'GET') {
	const requestHeaders = new Map<string, string>(Object.entries(headers));
	const fetchMock = vi.fn(async () => new Response('{}', { status: 200 }));

	const event = {
		params: { path },
		url: new URL(`http://frontend/api/v1/${path}`),
		request: {
			method,
			headers: requestHeaders,
			arrayBuffer: async () => new ArrayBuffer(0)
		},
		fetch: fetchMock
	} as unknown as Parameters<typeof GET>[0];

	return { event, fetchMock };
}

describe('SSR proxy header allowlisting', () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it('forwards allowlisted headers (cookie, csrf token) and strips x-forwarded-*, authorization, and host', async () => {
		const { event, fetchMock } = makeEvent('machines', {
			'content-type': 'application/json',
			cookie: 'vord_auth=token; vord_tenant=7',
			'x-csrf-token': 'csrf-token-abc',
			'x-forwarded-for': '1.2.3.4',
			'x-forwarded-host': 'evil.example.com',
			authorization: 'Bearer client-supplied',
			host: 'evil.example.com'
		});

		await GET(event);

		expect(fetchMock).toHaveBeenCalledTimes(1);
		const init = fetchMock.mock.calls[0][1] as RequestInit;
		const forwarded = init.headers as Headers;

		expect(forwarded.get('cookie')).toBe('vord_auth=token; vord_tenant=7');
		expect(forwarded.get('content-type')).toBe('application/json');
		// The backend's JSON antiforgery gate requires this header on cookie-authenticated mutations.
		expect(forwarded.get('x-csrf-token')).toBe('csrf-token-abc');
		expect(forwarded.get('x-forwarded-for')).toBeNull();
		expect(forwarded.get('x-forwarded-host')).toBeNull();
		expect(forwarded.get('authorization')).toBeNull();
		expect(forwarded.get('host')).toBeNull();
	});

	it('targets the configured upstream base with the request path and query', async () => {
		const { event, fetchMock } = makeEvent('machines', { accept: 'application/json' });
		event.url.search = '?page=2';

		await GET(event);

		const calledUrl = fetchMock.mock.calls[0][0] as string;
		expect(calledUrl).toBe('http://backend:12233/api/v1/machines?page=2');
	});

	it('rejects a path containing a traversal segment with 400', async () => {
		const { event, fetchMock } = makeEvent('machines/../../etc/passwd', {
			cookie: 'vord_auth=token'
		});

		await expect(GET(event)).rejects.toMatchObject({ status: 400 });
		expect(fetchMock).not.toHaveBeenCalled();
	});

	it('forwards the body on mutating requests through the allowlist', async () => {
		const { event, fetchMock } = makeEvent(
			'integrations/5',
			{ 'content-type': 'application/json', authorization: 'Bearer spoof' },
			'PUT'
		);

		await PUT(event);

		const init = fetchMock.mock.calls[0][1] as RequestInit;
		const forwarded = init.headers as Headers;
		expect(forwarded.get('content-type')).toBe('application/json');
		expect(forwarded.get('authorization')).toBeNull();
		expect(init.body).toBeDefined();
	});
});
