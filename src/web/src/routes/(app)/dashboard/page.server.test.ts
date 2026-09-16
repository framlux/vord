// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MAX_PAGE_SIZE } from '$lib/utils/constants';

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getFleetOverview: vi.fn()
	}
}));

vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock
}));

import { load } from './+page.server';

type LoadEvent = Parameters<typeof load>[0];

function makeLoadEvent(query: string): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		url: new URL(`http://localhost/dashboard${query}`)
	} as unknown as LoadEvent;
}

// The loader reads a URL a person may have typed. The API refuses an over-limit page outright, so
// the loader must never forward one — but it clamps rather than refusing, because the page paginates
// on the metadata the API returns and a shorter page still reaches every machine.
describe('dashboard +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getFleetOverview.mockResolvedValue({ machines: [], totalPages: 1 });
	});

	it('never asks the API for more than it serves, however large the typed page size', async () => {
		await load(makeLoadEvent('?pageSize=500'));

		expect(apiMock.getFleetOverview).toHaveBeenCalledWith(expect.objectContaining({ pageSize: MAX_PAGE_SIZE }));
	});

	it('honours a page size within range, and defaults one that was not given', async () => {
		await load(makeLoadEvent('?pageSize=40'));
		expect(apiMock.getFleetOverview).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 40 }));

		await load(makeLoadEvent(''));
		expect(apiMock.getFleetOverview).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }));
	});

	it('floors a nonsensical page and page size instead of forwarding them', async () => {
		await load(makeLoadEvent('?page=-3&pageSize=0'));

		expect(apiMock.getFleetOverview).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 1 }));
	});
});
