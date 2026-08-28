// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// Hoisted so the vi.mock factory below, which is itself hoisted, can close over it.
const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getAdminUsers: vi.fn(),
		getAdminSettings: vi.fn(),
		getTenants: vi.fn()
	}
}));
vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock,
	csrfFor: () => undefined
}));

import { load } from './+page.server';

type LoadEvent = Parameters<typeof load>[0];

function makeEvent(selfHosted: boolean | undefined): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		locals: {
			user: {
				isGlobalAdmin: true,
				deployment: selfHosted === undefined ? undefined : { selfHosted }
			}
		}
	} as unknown as LoadEvent;
}

/** Takes the awaited value rather than a Promise, because `load` is typed MaybePromise. */
async function statusOf(result: unknown): Promise<number> {
	try {
		await result;
	} catch (thrown) {
		return (thrown as { status: number }).status;
	}

	throw new Error('expected the load to throw, but it resolved');
}

describe('admin +page.server load — deployment mode', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getAdminUsers.mockResolvedValue([]);
		apiMock.getAdminSettings.mockResolvedValue([]);
		apiMock.getTenants.mockResolvedValue([]);
	});

	it('loads the admin data in a self-hosted deployment', async () => {
		const data = await load(makeEvent(true));

		expect(data).toMatchObject({ users: [], settings: [], tenants: [] });
		expect(apiMock.getAdminUsers).toHaveBeenCalledOnce();
		expect(apiMock.getAdminSettings).toHaveBeenCalledOnce();
		expect(apiMock.getTenants).toHaveBeenCalledOnce();
	});

	it('404s in hosted mode without calling the admin endpoints', async () => {
		expect(await statusOf(load(makeEvent(false)))).toBe(404);

		// Reaching the API would mean a 404 from the api-server surfacing as an unhandled 500,
		// because the catch below only translates 401 and 403.
		expect(apiMock.getAdminUsers).not.toHaveBeenCalled();
		expect(apiMock.getAdminSettings).not.toHaveBeenCalled();
		expect(apiMock.getTenants).not.toHaveBeenCalled();
	});

	it('treats an absent deployment field as hosted and 404s', async () => {
		// An older api-server omits the field entirely. The only place a version mismatch can
		// occur is the hosted cluster mid-rollout, where this route must not exist.
		expect(await statusOf(load(makeEvent(undefined)))).toBe(404);
		expect(apiMock.getAdminUsers).not.toHaveBeenCalled();
	});
});
