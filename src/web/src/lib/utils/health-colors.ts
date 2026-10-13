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

// Not a health status: a paused host is outside every health bucket, so its state has no
// MachineHealthStatus value and is drawn from this set instead.
export const pausedColors: HealthColorSet = {
	hex: '#6366F1',
	hexMuted: '#6366F133',
	bg: 'bg-indigo-100 dark:bg-indigo-900/30',
	text: 'text-indigo-800 dark:text-indigo-300',
	dot: 'bg-indigo-500',
	label: 'Paused'
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

// Left border of a machine row. Shared by every list that shows machines so a row reads the same on
// the dashboard, the machine list and the fleet search.
export function healthBorderClass(status: MachineHealthStatus, isPaused: boolean): string {
	// A paused host's health is frozen, so its border must not claim one.
	if (isPaused) {
		return 'border-l-2 border-l-indigo-500';
	}

	switch (status) {
		case MachineHealthStatus.Healthy:
			return 'border-l-2 border-l-green-500';
		case MachineHealthStatus.Warning:
			return 'border-l-2 border-l-amber-500';
		case MachineHealthStatus.Critical:
			return 'border-l-2 border-l-red-500';
		case MachineHealthStatus.Offline:
			return 'border-l-2 border-l-gray-300 dark:border-l-gray-600';
		default:
			return 'border-l-2 border-l-transparent';
	}
}

export function getVitalColor(percent: number): string {
	if (percent >= 95) return '#FF6467';
	if (percent >= 80) return '#FBBF24';

	return '#10B981';
}

/// Returns a human-readable severity label for screen readers.
export function getVitalSeverity(percent: number): string {
	if (percent >= 95) return 'critical';
	if (percent >= 80) return 'warning';

	return 'normal';
}

