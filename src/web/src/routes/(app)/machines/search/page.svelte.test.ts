// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus } from '$lib/api/types';
import type { FleetMachineDto, MachineSearchParams, PaginatedResponse, UserDto } from '$lib/api/types';

vi.mock('$app/navigation', () => ({
	goto: vi.fn()
}));

import SearchPage from './+page.svelte';

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

function makeData(items: FleetMachineDto[], filters: MachineSearchParams = {}) {
	const results: PaginatedResponse<FleetMachineDto> = {
		items,
		page: 1,
		pageSize: 25,
		totalCount: items.length,
		totalPages: 1,
		hasNextPage: false,
		hasPreviousPage: false
	};

	return { user: makeUser(), subscription: null, results, filters };
}

describe('machine search results', () => {
	it('shows a paused host as Paused, not as the Critical health it froze at', () => {
		render(SearchPage, {
			props: {
				data: makeData([makeMachine({ id: 9, name: 'frozen-box', healthStatus: MachineHealthStatus.Critical, isPaused: true })])
			}
		});

		const row = screen.getByRole('row', { name: /frozen-box/ });

		expect(within(row).getByLabelText('Status: Paused')).toBeInTheDocument();
		expect(within(row).queryByLabelText('Status: Critical')).not.toBeInTheDocument();
		expect(row.className).toContain('border-l-indigo-500');
		expect(row.className).not.toContain('border-l-red-500');
	});

	it('keeps the health badge and border for an active host', () => {
		render(SearchPage, {
			props: {
				data: makeData([makeMachine({ name: 'hot-box', healthStatus: MachineHealthStatus.Critical })])
			}
		});

		const row = screen.getByRole('row', { name: /hot-box/ });

		expect(within(row).getByLabelText('Status: Critical')).toBeInTheDocument();
		expect(row.className).toContain('border-l-red-500');
	});

	it('shows the paused filter as a chip when the page was opened with it', () => {
		render(SearchPage, { props: { data: makeData([], { paused: 'true' }) } });

		expect(screen.getByRole('button', { name: 'Remove Status filter' })).toBeInTheDocument();
		expect(screen.getAllByText('Paused').length).toBeGreaterThanOrEqual(1);
	});
});
