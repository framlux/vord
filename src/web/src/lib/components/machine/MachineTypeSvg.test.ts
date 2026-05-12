// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/svelte';
import MachineTypeSvg from './MachineTypeSvg.svelte';
import { MachineType, MachineHealthStatus } from '$lib/api/types';

describe('MachineTypeSvg', () => {
	it('should render an SVG element', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Healthy,
				isOnline: true,
				size: 160
			}
		});

		const svg = container.querySelector('svg');
		expect(svg).not.toBeNull();
	});

	it('should use green color for online LED when isOnline is true', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Healthy,
				isOnline: true,
				size: 160
			}
		});

		// The online indicator dot uses fill="#10B981" (green) when online
		const greenLeds = container.querySelectorAll('circle[fill="#10B981"]');
		expect(greenLeds.length).toBeGreaterThan(0);
	});

	it('should use gray color for LED when isOnline is false', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Offline,
				isOnline: false,
				size: 160
			}
		});

		// The offline indicator dot uses fill="#6E6E77" (gray) when offline
		const grayLeds = container.querySelectorAll('circle[fill="#6E6E77"]');
		expect(grayLeds.length).toBeGreaterThan(0);

		// No green LEDs should be present
		const greenLeds = container.querySelectorAll('circle[fill="#10B981"]');
		expect(greenLeds.length).toBe(0);
	});

	it('should apply machine-led-pulse class only when online', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Healthy,
				isOnline: true,
				size: 160
			}
		});

		const pulsingElements = container.querySelectorAll('.machine-led-pulse');
		expect(pulsingElements.length).toBeGreaterThan(0);
	});

	it('should not apply machine-led-pulse class when offline', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Offline,
				isOnline: false,
				size: 160
			}
		});

		const pulsingElements = container.querySelectorAll('.machine-led-pulse');
		expect(pulsingElements.length).toBe(0);
	});

	it('should use health-specific colors for ring and traces based on healthStatus', () => {
		// Healthy uses green (#10B981) for the gradient
		const { container: healthyContainer } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Healthy,
				isOnline: true,
				size: 160
			}
		});

		const healthyStops = healthyContainer.querySelectorAll('stop[stop-color="#10B981"]');
		expect(healthyStops.length).toBeGreaterThan(0);

		// Offline uses gray (#6E6E77) for the gradient
		const { container: offlineContainer } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.BareMetalServer,
				healthStatus: MachineHealthStatus.Offline,
				isOnline: false,
				size: 160
			}
		});

		const offlineStops = offlineContainer.querySelectorAll('stop[stop-color="#6E6E77"]');
		expect(offlineStops.length).toBeGreaterThan(0);
	});

	it('should set aria-label to Online when isOnline is true', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.Desktop,
				healthStatus: MachineHealthStatus.Healthy,
				isOnline: true,
				size: 160
			}
		});

		const svg = container.querySelector('svg');
		expect(svg?.getAttribute('aria-label')).toBe('Online machine visualization');
	});

	it('should set aria-label to Offline when isOnline is false', () => {
		const { container } = render(MachineTypeSvg, {
			props: {
				machineType: MachineType.Desktop,
				healthStatus: MachineHealthStatus.Offline,
				isOnline: false,
				size: 160
			}
		});

		const svg = container.querySelector('svg');
		expect(svg?.getAttribute('aria-label')).toBe('Offline machine visualization');
	});

	it('should render different machine types without errors', () => {
		const types = [
			MachineType.BareMetalServer,
			MachineType.Desktop,
			MachineType.Laptop,
			MachineType.VirtualMachine,
			MachineType.Unknown
		];

		for (const machineType of types) {
			const { container } = render(MachineTypeSvg, {
				props: {
					machineType,
					healthStatus: MachineHealthStatus.Healthy,
					isOnline: true,
					size: 160
				}
			});

			const svg = container.querySelector('svg');
			expect(svg).not.toBeNull();
		}
	});
});
