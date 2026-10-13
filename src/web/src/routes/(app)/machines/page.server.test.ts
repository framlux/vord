// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getMachines: vi.fn()
	}
}));

vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock
}));

import { load } from './+page.server';

type LoadEvent = Parameters<typeof load>[0];
type LoadResult = { filters: { status: string } };

function makeLoadEvent(query: string): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		url: new URL(`http://localhost/machines${query}`)
	} as unknown as LoadEvent;
}

// The status in the URL may be typed or bookmarked in any case, but the page's filter only selects
// the capitalised option, so the loader settles the case before the page and the API see it.
describe('machines +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getMachines.mockResolvedValue({ items: [], totalPages: 1 });
	});

	it.each([
		['paused', 'Paused'],
		['PAUSED', 'Paused'],
		['Paused', 'Paused'],
		['online', 'Online'],
		['Online', 'Online'],
		['critical', 'Critical'],
		['offline', 'Offline'],
		['healthy', 'Healthy'],
		['warning', 'Warning']
	])('turns ?status=%s into the %s option', async (typed, expected) => {
		const result = (await load(makeLoadEvent(`?status=${typed}`))) as LoadResult;

		expect(apiMock.getMachines).toHaveBeenCalledWith(expect.objectContaining({ status: expected }));
		expect(result.filters.status).toBe(expected);
	});

	it.each(['bogus', 'all', '', ' '])('applies no status filter for ?status=%s', async (typed) => {
		const result = (await load(makeLoadEvent(`?status=${typed}`))) as LoadResult;

		expect(apiMock.getMachines.mock.calls[0][0].status).toBeUndefined();
		expect(result.filters.status).toBe('');
	});

	it('applies no status filter when none was given', async () => {
		const result = (await load(makeLoadEvent(''))) as LoadResult;

		expect(apiMock.getMachines.mock.calls[0][0].status).toBeUndefined();
		expect(result.filters.status).toBe('');
	});
});
