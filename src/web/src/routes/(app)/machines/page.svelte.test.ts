// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus, MachineType, OperatingSystem } from '$lib/api/types';
import type { MachineDto, PaginatedResponse, UserDto } from '$lib/api/types';

vi.mock('$app/navigation', () => ({
	invalidateAll: vi.fn(),
	goto: vi.fn()
}));

import MachinesPage from './+page.svelte';

function makeMachine(overrides: Partial<MachineDto> = {}): MachineDto {
	return {
		id: 1,
		name: 'web-01',
		description: null,
		location: null,
		hostname: 'web-01.acme.co',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.VirtualMachine,
		serialNumber: 'SN-1',
		assetTag: null,
		isOnline: true,
		healthStatus: MachineHealthStatus.Healthy,
		lastPing: '2026-08-28T10:00:00Z',
		registeredOn: '2026-01-01T00:00:00Z',
		isDeleted: false,
		commandsEnabled: true,
		isPaused: false,
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

function makeData(items: MachineDto[]) {
	const machines: PaginatedResponse<MachineDto> = {
		items,
		page: 1,
		pageSize: 25,
		totalCount: items.length,
		totalPages: 1,
		hasNextPage: false,
		hasPreviousPage: false
	};

	return {
		machines,
		filters: { search: '', os: '', type: '', status: '' },
		user: makeUser(),
		subscription: null
	};
}

function makeDataWithStatus(items: MachineDto[], status: string) {
	const data = makeData(items);

	return { ...data, filters: { ...data.filters, status } };
}

describe('machines list', () => {
	it('shows an online machine whose health is Critical as Critical, not Online', () => {
		render(MachinesPage, {
			props: {
				data: makeData([
					makeMachine({ id: 7, name: 'hot-box', isOnline: true, healthStatus: MachineHealthStatus.Critical })
				])
			}
		});

		const row = screen.getByRole('row', { name: /hot-box/ });

		expect(within(row).getByLabelText('Status: Critical')).toHaveTextContent('Critical');
		expect(within(row).queryByLabelText('Status: Online')).not.toBeInTheDocument();
		expect(row.className).toContain('border-l-red-500');
	});

	it.each([
		{ health: MachineHealthStatus.Healthy, label: 'Healthy', border: 'border-l-green-500' },
		{ health: MachineHealthStatus.Warning, label: 'Warning', border: 'border-l-amber-500' },
		{ health: MachineHealthStatus.Critical, label: 'Critical', border: 'border-l-red-500' }
	])('shows a reachable machine at its $label health with a matching row border', ({ health, label, border }) => {
		render(MachinesPage, {
			props: { data: makeData([makeMachine({ id: 3, name: 'reachable-box', isOnline: true, healthStatus: health })]) }
		});

		const row = screen.getByRole('row', { name: /reachable-box/ });

		expect(within(row).getByLabelText(`Status: ${label}`)).toHaveTextContent(label);
		expect(row.className).toContain(border);
	});

	it('shows a machine the server has lost contact with as Offline', () => {
		render(MachinesPage, {
			props: {
				data: makeData([
					makeMachine({ id: 2, name: 'down-box', isOnline: false, healthStatus: MachineHealthStatus.Offline })
				])
			}
		});

		const row = screen.getByRole('row', { name: /down-box/ });

		expect(within(row).getByLabelText('Status: Offline')).toHaveTextContent('Offline');
		expect(row.className).toContain('border-l-gray-300');
	});

	it('shows a paused host as Paused rather than its health, because its heartbeat still arrives', () => {
		render(MachinesPage, {
			props: {
				data: makeData([
					makeMachine({
						id: 4,
						name: 'old-box',
						isOnline: true,
						isPaused: true,
						healthStatus: MachineHealthStatus.Critical
					})
				])
			}
		});

		const row = screen.getByRole('row', { name: /old-box/ });

		expect(within(row).getByLabelText('Status: Paused')).toHaveTextContent('Paused');
		expect(within(row).queryByLabelText('Status: Critical')).not.toBeInTheDocument();
		expect(within(row).queryByLabelText('Status: Online')).not.toBeInTheDocument();
		expect(row.className).toContain('border-l-indigo-500');
	});

	it('no longer words any row as Online, because the list shows health', () => {
		render(MachinesPage, {
			props: {
				data: makeData([
					makeMachine({ id: 1, name: 'up-box', isOnline: true, healthStatus: MachineHealthStatus.Healthy }),
					makeMachine({ id: 2, name: 'down-box', isOnline: false, healthStatus: MachineHealthStatus.Offline })
				])
			}
		});

		expect(within(screen.getByRole('row', { name: /up-box/ })).getByLabelText('Status: Healthy')).toBeInTheDocument();
		expect(within(screen.getByRole('row', { name: /down-box/ })).getByLabelText('Status: Offline')).toBeInTheDocument();
		expect(screen.queryByLabelText('Status: Online')).not.toBeInTheDocument();
	});

	it('offers the status filter the badges use, with the names the API takes', () => {
		render(MachinesPage, { props: { data: makeData([makeMachine()]) } });

		const options = within(screen.getByLabelText('Filter by status')).getAllByRole('option');

		expect(options.map((option) => option.textContent?.trim())).toEqual([
			'All Status',
			'Healthy',
			'Warning',
			'Critical',
			'Offline',
			'Online (any health)',
			'Paused'
		]);
		expect(options.map((option) => (option as HTMLOptionElement).value)).toEqual([
			'',
			'Healthy',
			'Warning',
			'Critical',
			'Offline',
			'Online',
			'Paused'
		]);
	});

	it.each(['Paused', 'Online', 'Critical'])('selects the %s option when the list was reached with that status', (status) => {
		render(MachinesPage, { props: { data: makeDataWithStatus([makeMachine()], status) } });

		expect(screen.getByLabelText('Filter by status')).toHaveValue(status);
	});

	it('selects All Status when no status filter is applied', () => {
		render(MachinesPage, { props: { data: makeData([makeMachine()]) } });

		expect(screen.getByLabelText('Filter by status')).toHaveValue('');
	});
});
