// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiError } from '$lib/api/client';

const { apiMock, createServerApiClient, csrfFor } = vi.hoisted(() => {
	const apiMock = {
		getCluster: vi.fn(),
		renameCluster: vi.fn()
	};

	return {
		apiMock,
		createServerApiClient: vi.fn(() => apiMock),
		csrfFor: vi.fn(() => ({ cookie: 'csrf-cookie', token: 'csrf-token' }))
	};
});

vi.mock('$lib/api/server', () => ({ createServerApiClient, csrfFor }));

import { load, actions } from './+page.server';

type LoadEvent = Parameters<typeof load>[0];
type RenameEvent = Parameters<typeof actions.rename>[0];

function makeLoadEvent(clusterId: string): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		params: { clusterId }
	} as unknown as LoadEvent;
}

function makeRenameEvent(clusterId: string, fields: Record<string, string>): RenameEvent {
	const data = new FormData();
	for (const [key, value] of Object.entries(fields)) {
		data.append(key, value);
	}

	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		locals: { user: null },
		params: { clusterId },
		request: { formData: async () => data }
	} as unknown as RenameEvent;
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

describe('cluster detail +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it('loads the cluster named in the route', async () => {
		apiMock.getCluster.mockResolvedValue({ id: 12, name: 'Production', nodes: [] });

		const data = await load(makeLoadEvent('12'));

		expect(apiMock.getCluster).toHaveBeenCalledWith(12);
		expect(data).toMatchObject({ cluster: { id: 12, name: 'Production' } });
	});

	it('answers a cluster id that is not a number with 404 without calling the API', async () => {
		const thrown = await thrownBy(load(makeLoadEvent('not-a-number')));

		expect(thrown.status).toBe(404);
		expect(thrown.body?.message).toBe('Cluster not found');
		expect(apiMock.getCluster).not.toHaveBeenCalled();
	});

	it('answers a cluster id that is not a whole number with 404 without calling the API', async () => {
		const thrown = await thrownBy(load(makeLoadEvent('1.5')));

		expect(thrown.status).toBe(404);
		expect(apiMock.getCluster).not.toHaveBeenCalled();
	});

	it('maps a missing cluster to Cluster not found', async () => {
		apiMock.getCluster.mockRejectedValue(new ApiError(404, 'Not found'));

		const thrown = await thrownBy(load(makeLoadEvent('99')));

		expect(thrown.status).toBe(404);
		expect(thrown.body?.message).toBe('Cluster not found');
	});

	it('sends an unauthenticated visitor to the login page', async () => {
		apiMock.getCluster.mockRejectedValue(new ApiError(401, 'Unauthorized'));

		const thrown = await thrownBy(load(makeLoadEvent('12')));

		expect(thrown.status).toBe(302);
		expect(thrown.location).toBe('/auth/login');
	});

	it('refuses a forbidden visitor with Access denied', async () => {
		apiMock.getCluster.mockRejectedValue(new ApiError(403, 'Forbidden'));

		const thrown = await thrownBy(load(makeLoadEvent('12')));

		expect(thrown.status).toBe(403);
		expect(thrown.body?.message).toBe('Access denied');
	});

	it('does not swallow a server failure', async () => {
		apiMock.getCluster.mockRejectedValue(new ApiError(500, 'Server error (500)'));

		const thrown = await thrownBy(load(makeLoadEvent('12')));

		expect(thrown).toMatchObject({ status: 500 });
	});
});

describe('cluster detail +page.server rename action', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.renameCluster.mockResolvedValue({ id: 12 });
	});

	it('renames the cluster to the trimmed name', async () => {
		const result = await actions.rename(makeRenameEvent('12', { displayName: '  Production  ' }));

		expect(apiMock.renameCluster).toHaveBeenCalledWith(12, 'Production');
		expect(result).toEqual({ success: true });
	});

	it('resets the cluster to its default name when the input is blank', async () => {
		await actions.rename(makeRenameEvent('12', { displayName: '' }));

		expect(apiMock.renameCluster).toHaveBeenCalledWith(12, null);
	});

	it('resets the cluster to its default name when the input is only whitespace', async () => {
		await actions.rename(makeRenameEvent('12', { displayName: '   ' }));

		expect(apiMock.renameCluster).toHaveBeenCalledWith(12, null);
	});

	it('resets the cluster to its default name when the form omits the field', async () => {
		await actions.rename(makeRenameEvent('12', {}));

		expect(apiMock.renameCluster).toHaveBeenCalledWith(12, null);
	});

	it('builds its API client with the antiforgery pair, because the rename is a state-changing request', async () => {
		await actions.rename(makeRenameEvent('12', { displayName: 'Production' }));

		expect(csrfFor).toHaveBeenCalledOnce();
		expect(createServerApiClient).toHaveBeenCalledWith(
			expect.anything(),
			undefined,
			undefined,
			undefined,
			{ cookie: 'csrf-cookie', token: 'csrf-token' }
		);
	});

	it('refuses a cluster id that is not a number without calling the API', async () => {
		const result = await actions.rename(makeRenameEvent('abc', { displayName: 'Production' }));

		expect(result).toMatchObject({ status: 400, data: { message: 'Invalid cluster ID' } });
		expect(apiMock.renameCluster).not.toHaveBeenCalled();
	});

	it('returns the API message as a failure, with the typed name so the form can keep it', async () => {
		apiMock.renameCluster.mockRejectedValue(
			new ApiError(400, 'Display name must be 100 characters or fewer.')
		);

		const result = await actions.rename(makeRenameEvent('12', { displayName: 'x'.repeat(101) }));

		expect(result).toMatchObject({
			status: 400,
			data: { message: 'Display name must be 100 characters or fewer.', displayName: 'x'.repeat(101) }
		});
	});

	it('returns a forbidden rename as a failure rather than throwing', async () => {
		apiMock.renameCluster.mockRejectedValue(new ApiError(403, 'Forbidden'));

		const result = await actions.rename(makeRenameEvent('12', { displayName: 'Production' }));

		expect(result).toMatchObject({ status: 403, data: { message: 'Forbidden' } });
	});

	it('does not swallow a failure that is not an API error', async () => {
		apiMock.renameCluster.mockRejectedValue(new TypeError('network down'));

		await expect(actions.rename(makeRenameEvent('12', { displayName: 'Production' }))).rejects.toThrow(
			'network down'
		);
	});
});
