// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MachineHealthStatus, type FleetMachineDto } from '$lib/api/types';

// The sibling proxy suite pins dev=false to force the upstream path. vi.mock is hoisted per file,
// so mock mode cannot be exercised from there and needs a file of its own.
vi.mock('$app/environment', () => ({ dev: true }));
vi.mock('$env/dynamic/private', () => ({ env: { VORD_API_MOCK: 'true' } }));

// The fixture fleet is built inside vi.hoisted because the module mock below is hoisted above
// every top-level declaration, so a factory closing over an ordinary const reads it before it
// exists. Health values are the enum's numeric literals rather than the enum itself, so the
// fixture does not depend on import order inside the hoisted block.
const { fleet } = vi.hoisted(() => {
	function machine(id: number, name: string, healthStatus: number) {
		return {
			id,
			name,
			hostname: `${name}.prod.lan`,
			ipAddress: '10.0.1.1',
			hardwareModel: 'Dell PowerEdge R740',
			healthStatus,
			cpuUsagePercent: 10,
			memoryUsagePercent: 20,
			maxDiskUsagePercent: 30,
			hasDiskHealthIssue: false,
			hasHardwareIssue: false,
			isOnline: true,
			lastPing: null,
			pendingUpdates: 0,
			securityUpdates: 0,
			failedServices: 0,
			totalServices: 42
		};
	}

	return {
		fleet: [
			machine(1, 'web-01', 0),
			machine(2, 'web-02', 1),
			machine(3, 'db-01', 2)
		]
	};
});

vi.mock('$lib/api/mock-fixtures', () => ({
	mockUser: {},
	mockSubscription: {},
	mockFleetOverview: { summary: {} },
	mockFleetMachines: fleet,
	mockMachineList: { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 1 },
	mockMachineById: new Map(),
	mockMachineDetailById: new Map(),
	mockMachineAuthorizedKeys: [],
	mockFleetSshSessions: [],
	mockAlertRules: [],
	getMockMachineAlertRules: () => []
}));

import { GET } from './+server';

function makeEvent(path: string, query = '') {
	const fetchMock = vi.fn<typeof fetch>(async () => new Response('{}', { status: 200 }));

	const event = {
		params: { path },
		url: new URL(`http://frontend/api/v1/${path}${query}`),
		request: { method: 'GET', headers: new Map<string, string>(), arrayBuffer: async () => new ArrayBuffer(0) },
		fetch: fetchMock
	} as unknown as Parameters<typeof GET>[0];

	return { event, fetchMock };
}

async function getData(path: string, query = '') {
	const { event, fetchMock } = makeEvent(path, query);
	const response = await GET(event);
	const body = await response.json();

	// Mock mode must answer locally. A request that reached the upstream fetch would mean the
	// branch is missing and the picker is quietly talking to a backend that is not there.
	expect(fetchMock).not.toHaveBeenCalled();

	return body.data;
}

beforeEach(() => {
	vi.clearAllMocks();
});

describe('mock mode serves the machine endpoints the assignment picker drives', () => {
	// Without these two the picker renders a dialog that answers 404 to every request, which is
	// indistinguishable on screen from a tenant that owns no machines.
	it('serves machines/search rather than 404ing the picker', async () => {
		const data = await getData('machines/search');

		expect(data.items).toHaveLength(3);
		expect(data.totalCount).toBe(3);
	});

	it('serves machines/ids for select-all-matching', async () => {
		const data = await getData('machines/ids');

		expect(data.ids).toEqual([1, 2, 3]);
		expect(data.totalCount).toBe(3);
	});

	// The fixture fleet fits any cap. Saying so explicitly matters because a picker that trusted
	// mock mode to exercise the capped path would ship never having run it.
	it('reports the fixture selection as untruncated', async () => {
		const data = await getData('machines/ids');

		expect(data.truncated).toBe(false);
	});

	it('filters search by name, hostname and model', async () => {
		const data = await getData('machines/search', '?search=db');

		expect(data.items.map((m: FleetMachineDto) => m.name)).toEqual(['db-01']);
		expect(data.totalCount).toBe(1);
	});

	it('filters by health using the vocabulary the real endpoint accepts', async () => {
		const data = await getData('machines/search', '?healthStatus=critical');

		expect(data.items.map((m: FleetMachineDto) => m.name)).toEqual(['db-01']);
	});

	it('accepts the comma-separated multi-value health filter', async () => {
		const data = await getData('machines/search', '?healthStatus=warning,critical');

		expect(data.items.map((m: FleetMachineDto) => m.name)).toEqual(['web-02', 'db-01']);
	});

	// A picker that pages is only exercised by a source that pages, so mock mode pages for real
	// rather than returning the whole fixture fleet on every request.
	it('pages the result and reports the page count', async () => {
		const first = await getData('machines/search', '?page=1&pageSize=2');
		expect(first.items.map((m: FleetMachineDto) => m.name)).toEqual(['web-01', 'web-02']);
		expect(first.totalPages).toBe(2);
		expect(first.hasNextPage).toBe(true);

		const second = await getData('machines/search', '?page=2&pageSize=2');
		expect(second.items.map((m: FleetMachineDto) => m.name)).toEqual(['db-01']);
		expect(second.hasNextPage).toBe(false);
	});

	it('returns an empty page rather than failing when nothing matches', async () => {
		const data = await getData('machines/search', '?search=nothing-matches-this');

		expect(data.items).toEqual([]);
		expect(data.totalCount).toBe(0);
		expect(data.totalPages).toBe(1);
	});
});
