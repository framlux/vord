// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiError } from '$lib/api/client';

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getClusters: vi.fn()
	}
}));

vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock
}));

import { load } from './+page.server';

type LoadEvent = Parameters<typeof load>[0];

function makeLoadEvent(): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined }
	} as unknown as LoadEvent;
}

/** Takes the awaited value rather than a Promise, because `load` is typed MaybePromise. */
async function thrownBy(result: unknown): Promise<{ status: number; location?: string; body?: { message: string } }> {
	try {
		await result;
	} catch (thrown) {
		return thrown as { status: number; location?: string; body?: { message: string } };
	}

	throw new Error('expected the load to throw, but it resolved');
}

describe('clusters +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it('hands the page every cluster the API returns', async () => {
		apiMock.getClusters.mockResolvedValue([{ id: 1 }, { id: 2 }]);

		const data = await load(makeLoadEvent());

		expect(data).toMatchObject({ clusters: [{ id: 1 }, { id: 2 }] });
	});

	it('sends an unauthenticated visitor to the login page', async () => {
		apiMock.getClusters.mockRejectedValue(new ApiError(401, 'Unauthorized'));

		const thrown = await thrownBy(load(makeLoadEvent()));

		expect(thrown.status).toBe(302);
		expect(thrown.location).toBe('/auth/login');
	});

	it('refuses a forbidden visitor with Access denied', async () => {
		apiMock.getClusters.mockRejectedValue(new ApiError(403, 'Forbidden'));

		const thrown = await thrownBy(load(makeLoadEvent()));

		expect(thrown.status).toBe(403);
		expect(thrown.body?.message).toBe('Access denied');
	});

	it('does not swallow a server failure', async () => {
		apiMock.getClusters.mockRejectedValue(new ApiError(500, 'Server error (500)'));

		const thrown = await thrownBy(load(makeLoadEvent()));

		expect(thrown).toMatchObject({ status: 500 });
	});
});
