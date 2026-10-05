// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineType, OperatingSystem } from '$lib/api/types';
import type {
	AlertRuleDto,
	MachineDetailDto,
	MachineDto,
	SubscriptionDto,
	UserDto
} from '$lib/api/types';
import { MachineHealthStatus } from '$lib/api/types';

vi.mock('$app/navigation', () => ({
	invalidateAll: vi.fn(),
	goto: vi.fn()
}));

vi.mock('$app/forms', () => ({
	enhance: () => ({})
}));

vi.mock('$lib/crypto/signing', () => ({
	generateNonce: () => 'nonce',
	buildCanonicalPayload: () => '',
	signPayload: async () => '',
	getLocalKeys: async () => []
}));

import MachinePage from './+page.svelte';

function makeMachine(): MachineDto {
	return {
		id: 7,
		name: 'web-01',
		description: null,
		location: null,
		hostname: 'web-01.acme.co',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.VirtualMachine,
		serialNumber: 'SN-7',
		assetTag: null,
		isOnline: true,
		lastPing: '2026-08-28T10:00:00Z',
		registeredOn: '2026-01-01T00:00:00Z',
		isDeleted: false,
		commandsEnabled: true
	};
}

function makeRule(overrides: Partial<AlertRuleDto> = {}): AlertRuleDto {
	return {
		id: 1,
		name: 'Disk usage above 90%',
		description: null,
		metric: 'DiskUsage',
		operator: 'GreaterThan',
		threshold: 90,
		durationMinutes: 5,
		severity: 'Warning',
		isEnabled: false,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [],
		machines: [],
		...overrides
	};
}

function makeSubscription(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return {
		tier: 'Pro',
		status: 'Active',
		machineLimit: 1000,
		machineCount: 3,
		retentionDays: 60,
		currentPeriodEnd: null,
		cancelAtPeriodEnd: false,
		billingInterval: 'monthly',
		pendingAction: null,
		alertRuleLimit: 10,
		alertRuleCount: 0,
		webhookLimit: 3,
		webhookCount: 0,
		memberLimit: 5,
		memberCount: 1,
		...overrides
	};
}

function makeUser(selfHosted: boolean = false): UserDto {
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
		deployment: { selfHosted }
	};
}

function makeData(
	subscription: SubscriptionDto | null,
	allAlertRules: AlertRuleDto[],
	overrides: { machineAlertRules?: AlertRuleDto[]; selfHosted?: boolean } = {}
) {
	return {
		machine: makeMachine(),
		machineDetail: null,
		authorizedKeys: null,
		machineAlertRules: overrides.machineAlertRules ?? [],
		allAlertRules,
		subscription,
		user: makeUser(overrides.selfHosted ?? false)
	};
}

const builtIns: AlertRuleDto[] = Array.from({ length: 8 }, (_, i) =>
	makeRule({ id: i + 1, name: `Built-in ${i + 1}` })
);

describe('machine detail alert rules', () => {
	it('offers a Free admin an upgrade prompt instead of a rule picker the save would refuse', () => {
		// The list endpoint hands Free its eight disabled built-ins so it can read them as an upsell,
		// but the save endpoint is Pro-gated. A picker here would be eight checkboxes and a 403.
		render(MachinePage, {
			props: { data: makeData(makeSubscription({ tier: 'Free' }), builtIns) }
		});

		expect(screen.queryByRole('button', { name: 'Manage Rules' })).not.toBeInTheDocument();
		expect(screen.getByText(/only run on Pro and Team plans/i)).toBeInTheDocument();
	});

	it('offers no rule picker to a Pro admin whose subscription has lapsed', () => {
		render(MachinePage, {
			props: { data: makeData(makeSubscription({ status: 'PastDue' }), builtIns) }
		});

		expect(screen.queryByRole('button', { name: 'Manage Rules' })).not.toBeInTheDocument();
		expect(screen.getByText(/not active/i)).toBeInTheDocument();
	});

	it('gives an entitled Pro admin the rule picker with a checkbox per built-in', async () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription(), builtIns, { machineAlertRules: [builtIns[2]] })
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Manage Rules' }));

		expect(screen.getAllByRole('checkbox')).toHaveLength(8);
		expect(screen.getByRole('checkbox', { name: /Built-in 3/ })).toBeChecked();
		expect(screen.getByRole('checkbox', { name: /Built-in 1/ })).not.toBeChecked();
	});

	it('offers an Enterprise admin the custom rules Team is offered', async () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Enterprise' }), [
					...builtIns,
					makeRule({ id: 99, name: 'Custom load rule', isCustom: true })
				])
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Manage Rules' }));

		expect(screen.getByRole('checkbox', { name: /Custom load rule/ })).toBeInTheDocument();
	});

	it('tells a lapsed Enterprise admin the subscription is inactive instead of telling it to upgrade', () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Enterprise', status: 'Canceled' }), builtIns)
			}
		});

		expect(screen.queryByRole('button', { name: 'Manage Rules' })).not.toBeInTheDocument();
		expect(screen.getByText(/not active/i)).toBeInTheDocument();
		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
	});

	it('still shows a Free machine the rules it already carries, so the upsell has something to sell', () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Free' }), builtIns, {
					machineAlertRules: [makeRule({ id: 3, name: 'Built-in 3' })]
				})
			}
		});

		expect(screen.getByText('Built-in 3')).toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'Manage Rules' })).not.toBeInTheDocument();
	});

	it('offers a self-hosted deployment its custom rules, which it is entitled to author', async () => {
		// The picker decides what to offer from a bare tier comparison, while every other alert
		// control on this page asks canAuthorAlertRules. The two disagree exactly where the
		// subscription row is not the authority: a self-hosted deployment has no meaningful tier, is
		// entitled to everything, and would still have its own custom rules filtered out of the list.
		render(MachinePage, {
			props: {
				data: makeData(
					makeSubscription({ tier: 'Free', status: 'Canceled' }),
					[...builtIns, makeRule({ id: 99, name: 'Custom load rule', isCustom: true })],
					{ selfHosted: true }
				)
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Manage Rules' }));

		expect(screen.getByRole('checkbox', { name: /Custom load rule/ })).toBeInTheDocument();
	});

	it('says a machine carrying only disabled rules is watched by nothing', () => {
		// Not the alerts page's question read backwards. Every rule here names this machine, so an
		// assignment check would call it covered; none of them is switched on, so nothing will fire.
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription(), builtIns, {
					machineAlertRules: [makeRule({ id: 3, name: 'Built-in 3', isEnabled: false })]
				})
			}
		});

		expect(screen.getByText(/no enabled alert rule is watching this machine/i)).toBeInTheDocument();
	});

	it('says a machine with no rules at all is watched by nothing', () => {
		// This is the case the "new machines arrive unwatched" decision creates, and a fleet
		// expansion is exactly when nobody is reading the alerts page.
		render(MachinePage, {
			props: { data: makeData(makeSubscription(), builtIns, { machineAlertRules: [] }) }
		});

		expect(screen.getByText(/no enabled alert rule is watching this machine/i)).toBeInTheDocument();
	});

	it('says nothing about coverage once an enabled rule is watching the machine', () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription(), builtIns, {
					machineAlertRules: [makeRule({ id: 3, name: 'Built-in 3', isEnabled: true })]
				})
			}
		});

		expect(screen.queryByText(/no enabled alert rule is watching this machine/i)).not.toBeInTheDocument();
	});

	describe('the rule picker dialog is operable from the keyboard', () => {
		// It was announced as a modal dialog and behaved like a div. Nothing moved focus into it, so
		// its Escape handler — bound to the dialog element rather than the window — could not receive
		// the key at all, and there was no trap and no focus return. Every one of these fails against
		// that version.
		async function openPicker() {
			render(MachinePage, {
				props: { data: makeData(makeSubscription(), builtIns) }
			});

			// Focused before it is clicked, because a real activation — pointer or keyboard — focuses
			// the control, and jsdom's click does not. Without this the dialog is asked to restore
			// focus to a document body that never held it, and the restore looks broken when it is
			// the fixture that is unfaithful.
			const trigger = screen.getByRole('button', { name: 'Manage Rules' });
			trigger.focus();
			await fireEvent.click(trigger);

			return screen.getByRole('dialog');
		}

		it('moves focus into the dialog when it opens', async () => {
			const dialog = await openPicker();

			await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
		});

		it('closes on Escape', async () => {
			await openPicker();

			await fireEvent.keyDown(window, { key: 'Escape' });

			expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
		});

		it('wraps Tab on the last control back to the first', async () => {
			const dialog = await openPicker();
			const focusable = dialog.querySelectorAll<HTMLElement>(
				'button:not([disabled]), input:not([disabled]), select:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'
			);
			const first = focusable[0];
			const last = focusable[focusable.length - 1];

			last.focus();
			await fireEvent.keyDown(dialog, { key: 'Tab' });

			expect(document.activeElement).toBe(first);
		});

		it('returns focus to the control that opened it', async () => {
			await openPicker();
			const trigger = screen.getByRole('button', { name: 'Manage Rules' });

			await fireEvent.keyDown(window, { key: 'Escape' });

			await waitFor(() => expect(document.activeElement).toBe(trigger));
		});
	});

	it('leaves a self-hosted deployment its rule picker whatever its subscription row says', () => {
		render(MachinePage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Free', status: 'Canceled' }), builtIns, {
					selfHosted: true
				})
			}
		});

		expect(screen.getByRole('button', { name: 'Manage Rules' })).toBeInTheDocument();
		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
	});
});

function makeDetail(overrides: Partial<MachineDetailDto> = {}): MachineDetailDto {
	return {
		id: 7,
		name: 'web-01',
		hostname: 'web-01.acme.co',
		isOnline: true,
		lastPing: '2026-08-28T10:00:00Z',
		healthStatus: MachineHealthStatus.Healthy,
		systemInfo: {
			hostname: 'web-01.acme.co',
			uuid: 'uuid-7',
			cpuType: 'x86_64',
			cpuBrand: 'AMD EPYC 7763',
			cpuPhysicalCores: 32,
			cpuLogicalCores: 64,
			physicalMemory: 137_438_953_472,
			hardwareVendor: 'Dell',
			hardwareModel: 'R650',
			hardwareVersion: '1',
			hardwareSerial: 'SN-7',
			uptimeSeconds: 100_000,
			biosVersion: '2.1.0',
			ipAddresses: ['10.0.0.7']
		},
		osVersion: null,
		cpuUsage: {
			cpuUsagePercent: 61,
			userTime: 34,
			systemTime: 11,
			niceTime: 0,
			idleTime: 39,
			iowaitTime: 14,
			irqTime: 0,
			softirqTime: 2,
			stealTime: 0
		},
		memoryUsage: {
			memoryTotal: 137_438_953_472,
			memoryUsed: 68_719_476_736,
			memoryUsagePercent: 50
		},
		memoryInfo: {
			memoryTotal: 137_438_953_472,
			memoryFree: 34_359_738_368,
			memoryAvailable: 51_539_607_552,
			swapTotal: 0,
			swapFree: 0
		},
		memoryInfoReceivedAt: '2026-08-28T09:45:00Z',
		diskUsages: null,
		hardwareHealth: null,
		packageUpdates: null,
		failedServices: [],
		totalServices: 0,
		recentSshSessions: [],
		telemetryLastUpdated: '2026-08-28T10:00:00Z',
		agentVersion: null,
		...overrides
	};
}

function renderWithDetail(detail: MachineDetailDto) {
	return render(MachinePage, {
		props: {
			data: { ...makeData(makeSubscription(), builtIns), machineDetail: detail }
		}
	});
}

describe('machine detail CPU breakdown', () => {
	it('shows the user, system and iowait components of the CPU figure', () => {
		// The headline CPU percentage is 100 minus idle, so these are a decomposition of it. Without
		// them an operator cannot tell a machine busy with work from one stuck waiting on a disk.
		renderWithDetail(makeDetail());

		const breakdown = screen.getByTestId('cpu-breakdown');

		expect(breakdown).toHaveTextContent('user 34%');
		expect(breakdown).toHaveTextContent('sys 11%');
		expect(breakdown).toHaveTextContent('iowait 14%');
	});

	it('says the breakdown is part of the CPU figure rather than load on top of it', () => {
		renderWithDetail(makeDetail());

		expect(screen.getByTestId('cpu-breakdown')).toHaveTextContent(/not additional load/i);
	});

	it('hides the steal figure on a host reporting no steal', () => {
		// Permanently rendering "steal 0%" on bare metal trains people to ignore the one field that
		// matters on a noisy neighbour.
		renderWithDetail(makeDetail());

		expect(screen.getByTestId('cpu-breakdown')).not.toHaveTextContent('steal');
	});

	it('shows the steal figure once a hypervisor is taking time from the guest', () => {
		renderWithDetail(
			makeDetail({
				cpuUsage: {
					cpuUsagePercent: 61,
					userTime: 34,
					systemTime: 11,
					niceTime: 0,
					idleTime: 39,
					iowaitTime: 14,
					irqTime: 0,
					softirqTime: 2,
					stealTime: 7
				}
			})
		);

		expect(screen.getByTestId('cpu-breakdown')).toHaveTextContent('steal 7%');
	});

	it('renders no breakdown line at all for a machine that has reported no CPU usage', () => {
		renderWithDetail(makeDetail({ cpuUsage: null }));

		expect(screen.queryByTestId('cpu-breakdown')).not.toBeInTheDocument();
	});
});

describe('machine detail memory info', () => {
	async function openHardwareTab(detail: MachineDetailDto) {
		renderWithDetail(detail);
		await fireEvent.click(screen.getByRole('tab', { name: /hardware/i }));
	}

	it('renders available memory from the agent reading rather than total minus used', async () => {
		// total - used is the definition of used, so deriving it would restate a number already on
		// screen. MemAvailable is a different fact: 48 GB here, where total - used would say 64 GB.
		await openHardwareTab(makeDetail());

		expect(screen.getByTestId('memory-available')).toHaveTextContent('48 GB');
	});

	it('hides the swap line entirely on a host with no swap configured', async () => {
		await openHardwareTab(makeDetail());

		expect(screen.queryByTestId('memory-swap')).not.toBeInTheDocument();
	});

	it('renders swap used, total and percentage once swap is configured', async () => {
		await openHardwareTab(
			makeDetail({
				memoryInfo: {
					memoryTotal: 137_438_953_472,
					memoryFree: 34_359_738_368,
					memoryAvailable: 51_539_607_552,
					swapTotal: 8_589_934_592,
					swapFree: 2_147_483_648
				}
			})
		);

		const swap = screen.getByTestId('memory-swap');

		expect(swap).toHaveTextContent('6.0 GB');
		expect(swap).toHaveTextContent('8.0 GB');
		expect(swap).toHaveTextContent('75%');
	});

	it('labels the block with its own receipt time, not the machine last-seen time', async () => {
		// This record arrives every fifteen minutes and the detail lookup spans seven days, so an
		// unlabelled block could present week-old swap as current.
		await openHardwareTab(makeDetail());

		expect(screen.getByTestId('memory-info-as-of')).toBeInTheDocument();
	});

	it('renders nothing extra for a machine that has reported no memory info', async () => {
		await openHardwareTab(makeDetail({ memoryInfo: null, memoryInfoReceivedAt: null }));

		expect(screen.queryByTestId('memory-available')).not.toBeInTheDocument();
		expect(screen.queryByTestId('memory-swap')).not.toBeInTheDocument();
		expect(screen.queryByTestId('memory-info-as-of')).not.toBeInTheDocument();
	});
});
