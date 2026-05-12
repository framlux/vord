// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import MachineHero from './MachineHero.svelte';
import { MachineHealthStatus, MachineType, OperatingSystem } from '$lib/api/types';
import type { MachineDto } from '$lib/api/types';

function buildMachine(overrides?: Partial<MachineDto>): MachineDto {
	return {
		id: 1,
		name: 'test-machine',
		description: null,
		location: null,
		hostname: 'host-01',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'SN001',
		assetTag: null,
		isOnline: false,
		lastPing: null,
		registeredOn: '2026-01-01T00:00:00Z',
		isDeleted: false,
		commandsEnabled: false,
		...overrides
	};
}

describe('MachineHero', () => {
	it('should display Online text when isOnline is true', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: true,
				lastPing: '2026-05-12T10:00:00Z',
				healthStatus: MachineHealthStatus.Healthy
			}
		});

		expect(screen.getByText('Online')).toBeDefined();
	});

	it('should display Offline text when isOnline is false', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: false,
				lastPing: null,
				healthStatus: MachineHealthStatus.Offline
			}
		});

		// Both the HealthBadge and status text show "Offline"
		const offlineElements = screen.getAllByText('Offline');
		expect(offlineElements.length).toBeGreaterThanOrEqual(1);
	});

	it('should render Healthy badge when healthStatus is Healthy', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: true,
				lastPing: '2026-05-12T10:00:00Z',
				healthStatus: MachineHealthStatus.Healthy
			}
		});

		expect(screen.getByText('Healthy')).toBeDefined();
	});

	it('should render Critical badge when healthStatus is Critical', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: true,
				lastPing: '2026-05-12T10:00:00Z',
				healthStatus: MachineHealthStatus.Critical
			}
		});

		expect(screen.getByText('Critical')).toBeDefined();
	});

	it('should render Offline badge when healthStatus is Offline', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: false,
				lastPing: null,
				healthStatus: MachineHealthStatus.Offline
			}
		});

		// The HealthBadge renders "Offline" label for Offline status
		// and the status text also says "Offline", so we should find at least 2
		const offlineElements = screen.getAllByText('Offline');
		expect(offlineElements.length).toBeGreaterThanOrEqual(2);
	});

	it('should use healthStatus prop directly, not derive from detail', () => {
		// Regression test: healthStatus must come from the prop (polled value),
		// not be derived from a stale machineDetail object
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: true,
				lastPing: '2026-05-12T10:00:00Z',
				healthStatus: MachineHealthStatus.Warning
			}
		});

		expect(screen.getByText('Warning')).toBeDefined();
		expect(screen.getByText('Online')).toBeDefined();
	});

	it('should display machine name in heading', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine({ name: 'prod-web-01' }),
				isOnline: true,
				lastPing: null,
				healthStatus: MachineHealthStatus.Healthy
			}
		});

		expect(screen.getByText('prod-web-01')).toBeDefined();
	});

	it('should display last seen time when lastPing is provided', () => {
		render(MachineHero, {
			props: {
				machine: buildMachine(),
				isOnline: true,
				lastPing: '2026-05-12T10:00:00Z',
				healthStatus: MachineHealthStatus.Healthy
			}
		});

		const lastSeen = screen.getByText(/Last seen/);
		expect(lastSeen).toBeDefined();
	});
});
