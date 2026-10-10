// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineType, OperatingSystem } from '$lib/api/types';
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

describe('machines list', () => {
	it('shows a paused host as Paused rather than Online, because its heartbeat still arrives', () => {
		render(MachinesPage, {
			props: { data: makeData([makeMachine({ id: 4, name: 'old-box', isOnline: true, isPaused: true })]) }
		});

		const row = screen.getByRole('row', { name: /old-box/ });

		expect(within(row).getByLabelText('Status: Paused')).toHaveTextContent('Paused');
		expect(within(row).queryByLabelText('Status: Online')).not.toBeInTheDocument();
		expect(row.className).toContain('border-l-indigo-500');
	});

	it('keeps the Online and Offline pills for hosts that are not paused', () => {
		render(MachinesPage, {
			props: {
				data: makeData([
					makeMachine({ id: 1, name: 'up-box', isOnline: true }),
					makeMachine({ id: 2, name: 'down-box', isOnline: false })
				])
			}
		});

		expect(within(screen.getByRole('row', { name: /up-box/ })).getByLabelText('Status: Online')).toBeInTheDocument();
		expect(within(screen.getByRole('row', { name: /down-box/ })).getByLabelText('Status: Offline')).toBeInTheDocument();
	});

	it('offers Paused as a status filter', () => {
		render(MachinesPage, { props: { data: makeData([makeMachine()]) } });

		expect(within(screen.getByLabelText('Filter by status')).getByRole('option', { name: 'Paused' })).toHaveValue('Paused');
	});
});
