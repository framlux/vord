// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { MockApiClient } from './mock-client';
import { ApiError } from './client';
import { MachineHealthStatus } from './types';

// Mock mode is where the assignment picker is worked on without a live tenant, so it has to
// reproduce the one property the picker depends on: "select all matching" resolves exactly the set
// the list shows. It used to model the opposite — the ids call ignored every filter and read a
// different fixture fleet from the search call — so select-all under a filter selected everything,
// and the ids and rows could be disjoint sets.
//
// Filters are derived from whatever the fixtures contain rather than naming fixture machines, so
// these keep asserting agreement when the demo fleet changes.

describe('MockApiClient fleet queries', () => {
	it('resolves select-all to exactly the machines search lists, under every filter it can answer', async () => {
		const client = new MockApiClient();
		const all = await client.searchMachines({ page: 1, pageSize: 100 });
		const sample = all.items[0];

		const filters = [
			{},
			{ search: sample.name },
			{ healthStatus: MachineHealthStatus[sample.healthStatus].toLowerCase() }
		];

		for (const filter of filters) {
			const listed = await client.searchMachines({ page: 1, pageSize: 100, ...filter });
			const selected = await client.getMachineIds(filter);

			expect(selected.ids).toEqual(listed.items.map((m) => m.id));
			expect(selected.totalCount).toBe(listed.totalCount);
		}
	});

	it('narrows select-all when a filter narrows the list', async () => {
		const client = new MockApiClient();
		const all = await client.searchMachines({ page: 1, pageSize: 100 });
		const sample = all.items[0];

		const selected = await client.getMachineIds({ search: sample.name });

		expect(selected.ids).toContain(sample.id);
		expect(selected.ids.length).toBeLessThan(all.totalCount);
	});

	it('refuses a page larger than the API serves, as the API does', async () => {
		// A caller that regressed past the ceiling would otherwise pass in mock mode and meet a 400
		// in production.
		const client = new MockApiClient();

		const attempt = client.searchMachines({ page: 1, pageSize: 101 });

		await expect(attempt).rejects.toBeInstanceOf(ApiError);
		await expect(attempt).rejects.toMatchObject({ status: 400 });
	});

	it('serves a page exactly at the ceiling', async () => {
		const client = new MockApiClient();

		const page = await client.searchMachines({ page: 1, pageSize: 100 });

		expect(page.pageSize).toBe(100);
	});
});
