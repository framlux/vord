// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// vi.mock factories are hoisted above the module body, so anything they close over has to be
// hoisted with them or it is still in the temporal dead zone when the factory runs.
const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		getAlertRules: vi.fn(),
		getAlertEvents: vi.fn(),
		getIntegrations: vi.fn(),
		getIntegrationProviders: vi.fn(),
		getSubscription: vi.fn(),
		getMachines: vi.fn(),
		updateAlertRuleMachines: vi.fn(),
		updateAlertRule: vi.fn()
	}
}));

vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock,
	csrfFor: () => undefined
}));

import { load, actions } from './+page.server';
import { UserAccountRole } from '$lib/api/types';

type LoadEvent = Parameters<typeof load>[0];

function tenantAdmin() {
	return {
		isGlobalAdmin: false,
		activeTenantId: 1,
		tenants: [{ tenantId: 1, role: String(UserAccountRole.TenantAdmin) }]
	};
}

function makeLoadEvent(): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		locals: { user: tenantAdmin() },
		url: new URL('http://localhost/settings/alerts')
	} as unknown as LoadEvent;
}

function makeActionEvent(form: Record<string, string[]>) {
	const data = new FormData();
	for (const [key, values] of Object.entries(form)) {
		for (const value of values) {
			data.append(key, value);
		}
	}

	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		locals: { user: tenantAdmin() },
		request: { formData: async () => data }
	} as never;
}

function machinePage(count: number, totalCount: number) {
	return {
		items: Array.from({ length: count }, (_, i) => ({ id: i + 1, name: `machine-${i + 1}` })),
		page: 1,
		pageSize: 100,
		totalCount,
		totalPages: Math.ceil(totalCount / 100),
		hasNextPage: totalCount > count,
		hasPreviousPage: false
	};
}

describe('alerts +page.server load — the machine picker', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getAlertRules.mockResolvedValue([]);
		apiMock.getAlertEvents.mockResolvedValue(null);
		apiMock.getIntegrations.mockResolvedValue(null);
		apiMock.getIntegrationProviders.mockResolvedValue(null);
		apiMock.getSubscription.mockResolvedValue(null);
		apiMock.getMachines.mockResolvedValue(machinePage(3, 3));
	});

	it('asks for a page the machine list endpoint will actually return', async () => {
		// The endpoint clamps pageSize to 100. Asking for more was not an error, it was a silent
		// truncation, and the page then believed it was looking at the whole fleet.
		await load(makeLoadEvent());

		const requested = apiMock.getMachines.mock.calls[0][0] as { pageSize: number };
		expect(requested.pageSize).toBeLessThanOrEqual(100);
	});

	it('says nothing about truncation when the whole fleet fits', async () => {
		const data = await load(makeLoadEvent());

		expect(data).toMatchObject({ machinesTruncated: false, machineCount: 3 });
	});

	it('reports the truncation when the fleet does not fit on one page', async () => {
		apiMock.getMachines.mockResolvedValue(machinePage(100, 150));

		const data = await load(makeLoadEvent());

		expect(data).toMatchObject({ machinesTruncated: true, machineCount: 150 });
		expect((data as { machines: unknown[] }).machines).toHaveLength(100);
	});
});

describe('alerts +page.server actions — the machines the picker could offer', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.updateAlertRuleMachines.mockResolvedValue([]);
		apiMock.updateAlertRule.mockResolvedValue({});
	});

	it('tells the assignment API which machines the form was choosing from', async () => {
		// The API may only unassign machines the caller says it offered, so a form that omits its
		// offered set cannot remove anything at all.
		await actions.assignRuleMachines(
			makeActionEvent({ id: ['7'], machineIds: ['1', '3'], visibleMachineIds: ['1,2,3'] })
		);

		expect(apiMock.updateAlertRuleMachines).toHaveBeenCalledWith(7, {
			machineIds: [1, 3],
			visibleMachineIds: [1, 2, 3]
		});
	});

	it('tells the rule update API the same thing, because it replaces the set too', async () => {
		await actions.updateRule(
			makeActionEvent({
				id: ['7'],
				name: ['CPU'],
				metric: ['CpuUsage'],
				threshold: ['90'],
				durationMinutes: ['5'],
				severity: ['Warning'],
				machineIds: ['1'],
				visibleMachineIds: ['1,2']
			})
		);

		expect(apiMock.updateAlertRule).toHaveBeenCalledWith(
			7,
			expect.objectContaining({ machineIds: [1], visibleMachineIds: [1, 2] })
		);
	});
});
