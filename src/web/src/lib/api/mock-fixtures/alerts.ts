// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { AlertRuleDto } from '../types';

// Eight built-in rules covering the "obvious failures" the marketing copy promises.
// The machines field is left empty here; getMachineAlertRules returns the subset
// applied to a given host (see mockMachineAlertRules below).
export const mockAlertRules: AlertRuleDto[] = [
	{
		id: 1,
		name: 'Disk usage high',
		description: 'Any disk usage exceeds 80% for 15 minutes',
		metric: 'disk_usage_percent',
		operator: '>',
		threshold: 80,
		durationMinutes: 15,
		severity: 'warning',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 2,
		name: 'Disk SMART pre-fail',
		description: 'SMART status reports PRE-FAIL on any disk',
		metric: 'disk_smart_status',
		operator: '==',
		threshold: 1,
		durationMinutes: 0,
		severity: 'critical',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: true,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 3,
		name: 'Machine offline',
		description: 'No heartbeat received for 5 minutes',
		metric: 'last_ping_seconds',
		operator: '>',
		threshold: 300,
		durationMinutes: 0,
		severity: 'critical',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 4,
		name: 'CPU sustained high',
		description: 'CPU usage above 90% for 15 minutes',
		metric: 'cpu_usage_percent',
		operator: '>',
		threshold: 90,
		durationMinutes: 15,
		severity: 'warning',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 5,
		name: 'Memory near full',
		description: 'Memory usage above 90% for 10 minutes',
		metric: 'memory_usage_percent',
		operator: '>',
		threshold: 90,
		durationMinutes: 10,
		severity: 'warning',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 6,
		name: 'Failed SSH login',
		description: 'Any failed SSH authentication attempt',
		metric: 'ssh_failed_login',
		operator: '==',
		threshold: 1,
		durationMinutes: 0,
		severity: 'info',
		isEnabled: true,
		notifyEmail: false,
		notifyWebhook: true,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 7,
		name: 'Security update available',
		description: 'One or more security updates pending',
		metric: 'security_updates',
		operator: '>',
		threshold: 0,
		durationMinutes: 0,
		severity: 'info',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	},
	{
		id: 8,
		name: 'Temperature high',
		description: 'Any temperature sensor above 75°C for 5 minutes',
		metric: 'temperature_celsius',
		operator: '>',
		threshold: 75,
		durationMinutes: 5,
		severity: 'warning',
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: []
	}
];

// Rules assigned to a given machine. Most rules apply fleet-wide; a couple are
// scoped (e.g., temperature only meaningful on bare-metal).
export function getMockMachineAlertRules(machineId: number): AlertRuleDto[] {
	const universal = mockAlertRules.filter((r) => [1, 2, 3, 5, 6, 7].includes(r.id));
	const bareMetalOnly = mockAlertRules.filter((r) => [4, 8].includes(r.id));

	// VMs and the macOS workstation don't get bare-metal-flavored rules
	const skipBareMetal = new Set<number>([13, 14, 15, 18]);
	if (skipBareMetal.has(machineId)) {
		return universal;
	}

	return [...universal, ...bareMetalOnly];
}
