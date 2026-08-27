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

describe('admin +page.server load — deployment mode', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getAdminUsers.mockResolvedValue([]);
		apiMock.getAdminSettings.mockResolvedValue([]);
		apiMock.getTenants.mockResolvedValue([]);
	});

	it('passes selfHosted through so the page can hide the billing-only surfaces', async () => {
		const data = await load(makeEvent(true));

		expect(data).toMatchObject({ selfHosted: true });
	});

	it('reports hosted when the flag is false', async () => {
		const data = await load(makeEvent(false));

		expect(data).toMatchObject({ selfHosted: false });
	});

	it('treats an absent deployment field as hosted', async () => {
		// An older api-server omits the field entirely. The only place a version mismatch can
		// occur is the hosted cluster mid-rollout, so defaulting to self-hosted would hide the
		// billing surfaces from the operator of a deployment that does bill.
		const data = await load(makeEvent(undefined));

		expect(data).toMatchObject({ selfHosted: false });
	});

	it('loads the admin data regardless of mode', async () => {
		// The admin page itself exists in both modes; only what it renders differs.
		await load(makeEvent(true));

		expect(apiMock.getAdminUsers).toHaveBeenCalledOnce();
		expect(apiMock.getAdminSettings).toHaveBeenCalledOnce();
		expect(apiMock.getTenants).toHaveBeenCalledOnce();
	});
});
