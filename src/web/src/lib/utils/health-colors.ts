// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { MachineHealthStatus } from '$lib/api/types';

export interface HealthColorSet {
	hex: string;
	hexMuted: string;
	bg: string;
	text: string;
	dot: string;
	label: string;
}

const healthyColors: HealthColorSet = {
	hex: '#10B981',
	hexMuted: '#10B98133',
	bg: 'bg-green-100 dark:bg-green-900/30',
	text: 'text-green-800 dark:text-green-400',
	dot: 'bg-green-500',
	label: 'Healthy'
};

const warningColors: HealthColorSet = {
	hex: '#FBBF24',
	hexMuted: '#FBBF2433',
	bg: 'bg-amber-100 dark:bg-amber-900/30',
	text: 'text-amber-800 dark:text-amber-400',
	dot: 'bg-amber-500',
	label: 'Warning'
};

const criticalColors: HealthColorSet = {
	hex: '#FF6467',
	hexMuted: '#FF646733',
	bg: 'bg-red-100 dark:bg-red-900/30',
	text: 'text-red-800 dark:text-red-400',
	dot: 'bg-red-500',
	label: 'Critical'
};

const offlineColors: HealthColorSet = {
	hex: '#6E6E77',
	hexMuted: '#6E6E7733',
	bg: 'bg-gray-100 dark:bg-gray-800/50',
	text: 'text-gray-600 dark:text-gray-400',
	dot: 'bg-gray-400',
	label: 'Offline'
};

export function getHealthColors(status: MachineHealthStatus): HealthColorSet {
	switch (status) {
		case MachineHealthStatus.Healthy:
			return healthyColors;
		case MachineHealthStatus.Warning:
			return warningColors;
		case MachineHealthStatus.Critical:
			return criticalColors;
		case MachineHealthStatus.Offline:
			return offlineColors;
		default:
			return offlineColors;
	}
}

export function getVitalColor(percent: number): string {
	if (percent >= 95) return '#FF6467';
	if (percent >= 80) return '#FBBF24';

	return '#10B981';
}
