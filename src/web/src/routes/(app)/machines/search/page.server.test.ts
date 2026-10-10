// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		searchMachines: vi.fn()
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
		url: new URL(`http://localhost/machines/search${query}`)
	} as unknown as LoadEvent;
}

describe('machine search +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.searchMachines.mockResolvedValue({ items: [], totalPages: 1 });
	});

	it('forwards the paused filter when it is true', async () => {
		await load(makeLoadEvent('?paused=true'));

		expect(apiMock.searchMachines).toHaveBeenCalledWith(expect.objectContaining({ paused: 'true' }));
	});

	it.each(['false', '1', 'yes', ''])('does not forward a paused value of "%s"', async (value) => {
		await load(makeLoadEvent(`?paused=${value}`));

		const params = apiMock.searchMachines.mock.calls[0][0];

		expect(params).not.toHaveProperty('paused');
	});

	it('hands the page the paused filter it applied, so the panel and chips can show it', async () => {
		const result = (await load(makeLoadEvent('?paused=true'))) as { filters: { paused?: string } };

		expect(result.filters.paused).toBe('true');
	});

	it('keeps forwarding the health filter beside it, unchanged', async () => {
		await load(makeLoadEvent('?healthStatus=critical'));

		expect(apiMock.searchMachines).toHaveBeenCalledWith(expect.objectContaining({ healthStatus: 'critical' }));
	});
});
