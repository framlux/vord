// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus } from '$lib/api/types';
import type { FleetMachineDto, FleetSummaryDto, PaginatedFleetOverviewDto, UserDto } from '$lib/api/types';

vi.mock('$app/navigation', () => ({
	goto: vi.fn()
}));

import DashboardPage from './+page.svelte';

function makeMachine(overrides: Partial<FleetMachineDto> = {}): FleetMachineDto {
	return {
		id: 1,
		name: 'web-01',
		hostname: 'web-01.acme.co',
		ipAddress: '10.0.0.1',
		hardwareModel: null,
		healthStatus: MachineHealthStatus.Healthy,
		cpuUsagePercent: 10,
		memoryUsagePercent: 20,
		maxDiskUsagePercent: 30,
		hasDiskHealthIssue: false,
		hasHardwareIssue: false,
		isOnline: true,
		isPaused: false,
		lastPing: '2026-08-28T10:00:00Z',
		pendingUpdates: 0,
		securityUpdates: 0,
		failedServices: 0,
		totalServices: 5,
		...overrides
	};
}

function makeUser(): UserDto {
	return {
		id: 1,
		name: 'Jonathan Miller',
		email: 'jonathan@acme.co',
		avatar: '',
		isGlobalAdmin: false,
		uniqueId: 'uid-1',
		needsOnboarding: false,
		tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }],
		activeTenantId: 1,
		deployment: { selfHosted: false }
	};
}

function makeSummary(overrides: Partial<FleetSummaryDto> = {}): FleetSummaryDto {
	return {
		totalMachines: 10,
		onlineMachines: 6,
		offlineCount: 1,
		warningCount: 1,
		criticalCount: 1,
		pausedCount: 3,
		securityUpdates: 0,
		...overrides
	};
}

function makeData(machines: FleetMachineDto[], summary: FleetSummaryDto = makeSummary()) {
	const fleet: PaginatedFleetOverviewDto = {
		summary,
		machines,
		page: 1,
		pageSize: 25,
		totalCount: machines.length,
		totalPages: 1
	};

	return {
		user: makeUser(),
		subscription: null,
		fleet,
		page: 1,
		pageSize: 25,
		search: undefined,
		status: undefined,
		sortBy: 'name',
		sortDir: 'asc'
	};
}

describe('dashboard fleet summary', () => {
	it('shows the paused hosts in their own tile rather than folding them into a health count', () => {
		render(DashboardPage, { props: { data: makeData([]) } });

		expect(screen.getByText('Paused (30%)')).toBeInTheDocument();
		expect(screen.getByText('Paused (30%)').previousElementSibling).toHaveTextContent('3');
	});

	it('counts healthy hosts from the online total, which already leaves the paused ones out', () => {
		render(DashboardPage, { props: { data: makeData([]) } });

		// 6 online - 1 warning - 1 critical = 4 healthy; the 3 paused hosts are not in the 6.
		expect(screen.getByText('Healthy (40%)').previousElementSibling).toHaveTextContent('4');
	});

	it('includes the paused hosts in the donut description', () => {
		render(DashboardPage, { props: { data: makeData([]) } });

		expect(screen.getByRole('img', { name: /3 paused/ })).toBeInTheDocument();
	});

	it('draws a paused segment on the donut', () => {
		render(DashboardPage, { props: { data: makeData([]) } });

		const donut = screen.getByRole('img', { name: /Fleet health/ });

		// The indigo segment starts where the 30% paused share does, at 70% of the ring. jsdom writes
		// the colour as rgb, so the match is on that form.
		expect(donut.getAttribute('style')).toContain('rgb(99, 102, 241) 70%');
	});

	it('offers Paused as a status filter', () => {
		render(DashboardPage, { props: { data: makeData([]) } });

		expect(within(screen.getByLabelText('Filter by status')).getByRole('option', { name: 'Paused' })).toHaveValue('paused');
	});
});

describe('dashboard fleet rows', () => {
	it('shows a paused host as Paused with an indigo border, whatever health it froze at', () => {
		render(DashboardPage, {
			props: {
				data: makeData([makeMachine({ id: 9, name: 'frozen-box', healthStatus: MachineHealthStatus.Critical, isPaused: true })])
			}
		});

		const row = screen.getByRole('link', { name: 'View details for frozen-box' });

		expect(within(row).getByLabelText('Status: Paused')).toBeInTheDocument();
		expect(within(row).queryByLabelText('Status: Critical')).not.toBeInTheDocument();
		expect(row.className).toContain('border-l-indigo-500');
	});

	it('keeps the health badge for an active host', () => {
		render(DashboardPage, {
			props: { data: makeData([makeMachine({ name: 'hot-box', healthStatus: MachineHealthStatus.Warning })]) }
		});

		expect(
			within(screen.getByRole('link', { name: 'View details for hot-box' })).getByLabelText('Status: Warning')
		).toBeInTheDocument();
	});
});
