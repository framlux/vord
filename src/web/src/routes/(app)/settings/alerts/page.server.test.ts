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

describe('alerts +page.server load', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		apiMock.getAlertRules.mockResolvedValue([]);
		apiMock.getAlertEvents.mockResolvedValue(null);
		apiMock.getIntegrations.mockResolvedValue(null);
		apiMock.getIntegrationProviders.mockResolvedValue(null);
		apiMock.getSubscription.mockResolvedValue(null);
	});

	// This replaces three tests that pinned how honestly a first page of the fleet was described.
	// The picker no longer reads one — it pages the fleet itself through the search endpoint — so
	// the truncation they guarded against cannot arise here. What is worth pinning instead is that
	// the fetch does not come back: it costs a request on every visit that nothing would read, and
	// reintroducing it would put a partial view of the fleet back on the page.
	it('loads no machine list, because the picker reaches the fleet itself', async () => {
		const data = await load(makeLoadEvent());

		expect(apiMock.getMachines).not.toHaveBeenCalled();
		expect(data).not.toHaveProperty('machines');
		expect(data).not.toHaveProperty('machineCount');
		expect(data).not.toHaveProperty('machinesTruncated');
	});

	it('still loads the rules, which answer for every tier', async () => {
		apiMock.getAlertRules.mockResolvedValue([{ id: 1 }]);

		const data = await load(makeLoadEvent());

		// Matched rather than accessed: the loader's return type includes void, because it can
		// redirect or refuse instead of resolving.
		expect(data).toMatchObject({ rules: [{ id: 1 }] });
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
