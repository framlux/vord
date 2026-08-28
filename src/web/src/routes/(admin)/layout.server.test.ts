// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// Hoisted so the vi.mock factory below, which is itself hoisted, can close over it.
const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getSubscription: vi.fn()
	}
}));
vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock,
	csrfFor: () => undefined
}));

import { load } from './+layout.server';

type LoadEvent = Parameters<typeof load>[0];

function makeEvent(user: unknown): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		url: new URL('http://localhost/admin'),
		locals: { user }
	} as unknown as LoadEvent;
}

function globalAdmin(selfHosted: boolean | undefined) {
	return {
		isGlobalAdmin: true,
		deployment: selfHosted === undefined ? undefined : { selfHosted }
	};
}

/** Takes the awaited value rather than a Promise, because `load` is typed MaybePromise. */
async function thrownBy(result: unknown): Promise<unknown> {
	try {
		await result;
	} catch (thrown) {
		return thrown;
	}

	throw new Error('expected the load to throw, but it resolved');
}

async function statusOf(result: unknown): Promise<number> {
	return ((await thrownBy(result)) as { status: number }).status;
}

describe('(admin) +layout.server load — deployment mode', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getSubscription.mockResolvedValue(null);
	});

	it('allows a global admin in a self-hosted deployment', async () => {
		const data = await load(makeEvent(globalAdmin(true)));

		expect(data).toMatchObject({ user: { isGlobalAdmin: true } });
	});

	it('404s in hosted mode, because the operator console owns this surface there', async () => {
		expect(await statusOf(load(makeEvent(globalAdmin(false))))).toBe(404);
	});

	it('treats an absent deployment field as hosted and 404s', async () => {
		// An older api-server omits the field entirely, and only the hosted cluster can be
		// mid-rollout. Such a server also predates the REST gate, so its admin calls would all
		// succeed — guessing self-hosted here would render a fully working fleet-admin console
		// in the hosted deployment.
		expect(await statusOf(load(makeEvent(globalAdmin(undefined))))).toBe(404);
	});

	it('403s a non-admin before the mode is considered', async () => {
		// Ordering matters: a tenant user must not be able to distinguish a hosted deployment
		// from a self-hosted one by the status code they get back.
		const user = { isGlobalAdmin: false, deployment: { selfHosted: true } };

		expect(await statusOf(load(makeEvent(user)))).toBe(403);
	});

	it('redirects an unauthenticated visitor to the login page', async () => {
		const thrown = await thrownBy(load(makeEvent(null)));

		expect(thrown).toMatchObject({
			status: 302,
			location: '/auth/login?returnUrl=%2Fadmin'
		});
	});
});
