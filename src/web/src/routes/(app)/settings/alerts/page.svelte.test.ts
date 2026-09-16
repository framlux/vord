// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus } from '$lib/api/types';
import type {
	AlertRuleDto,
	FleetMachineDto,
	PaginatedResponse,
	SubscriptionDto,
	UserDto
} from '$lib/api/types';

const { mockPage } = vi.hoisted(() => {
	const mockPage = {
		url: new URL('http://localhost/settings/alerts'),
		params: {},
		route: { id: '/(app)/settings/alerts' },
		status: 200,
		error: null,
		data: {},
		form: null
	};

	return { mockPage };
});

vi.mock('$app/state', () => ({
	page: mockPage
}));

vi.mock('$app/navigation', () => ({
	goto: vi.fn()
}));

vi.mock('$app/forms', () => ({
	enhance: () => ({})
}));

// The assignment picker reads the fleet through the API client. Without a stub it reaches real
// fetch under jsdom, and every test that opens the picker fails on that rather than on the thing
// it is actually asserting.
const { searchMachinesMock, getMachineIdsMock } = vi.hoisted(() => ({
	searchMachinesMock: vi.fn(),
	getMachineIdsMock: vi.fn()
}));

vi.mock('$lib/api/client', () => ({
	ApiClient: class {
		searchMachines = searchMachinesMock;
		getMachineIds = getMachineIdsMock;
	}
}));

import AlertsPage from './+page.svelte';

function fleetMachine(id: number, name: string): FleetMachineDto {
	return {
		id,
		name,
		hostname: `${name}.prod.lan`,
		ipAddress: '10.0.1.1',
		hardwareModel: null,
		healthStatus: MachineHealthStatus.Healthy,
		cpuUsagePercent: 5,
		memoryUsagePercent: 5,
		maxDiskUsagePercent: 5,
		hasDiskHealthIssue: false,
		hasHardwareIssue: false,
		isOnline: true,
		lastPing: null,
		pendingUpdates: 0,
		securityUpdates: 0,
		failedServices: 0,
		totalServices: 1
	};
}

function fleetPage(items: FleetMachineDto[]): PaginatedResponse<FleetMachineDto> {
	return {
		items,
		page: 1,
		pageSize: 25,
		totalCount: items.length,
		totalPages: 1,
		hasNextPage: false
	} as PaginatedResponse<FleetMachineDto>;
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
		isEnabled: true,
		notifyEmail: true,
		notifyWebhook: false,
		isCustom: false,
		machineIds: [10],
		machines: [{ id: 10, name: 'web-01' }],
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
	rules: AlertRuleDto[],
	overrides: { selfHosted?: boolean } = {}
) {
	// Mirrors what the loader actually returns. It stopped sending a machine list once the picker
	// began reaching the fleet itself, and test data still carrying one would let a test pass
	// against a field production never supplies.
	return {
		rules,
		events: null,
		integrations: null,
		providers: null,
		subscription,
		filters: { status: undefined, severity: undefined },
		user: makeUser(overrides.selfHosted ?? false)
	};
}

describe('alerts settings page', () => {
	it('shows a Free tenant its built-in rules read-only, with an upgrade prompt', () => {
		const subscription = makeSubscription({ tier: 'Free', alertRuleLimit: 0, alertRuleCount: 0 });
		render(AlertsPage, {
			props: { data: makeData(subscription, [makeRule({ isEnabled: false })]) }
		});

		expect(screen.getByText('Disk usage above 90%')).toBeInTheDocument();
		expect(screen.getByText('Built-in')).toBeInTheDocument();
		expect(screen.getByText(/only run on Pro and Team plans/i)).toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Enable / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Disable / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Assign machines to / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'New Rule' })).not.toBeInTheDocument();
		expect(screen.queryByText('Limit reached')).not.toBeInTheDocument();
	});

	it('withholds every control from a Pro tenant whose subscription has lapsed', () => {
		// Each control here posts to an endpoint gated by RequiresPro, which refuses a non-Active
		// status as flatly as it refuses Free. A tier-only gate would render a working screen where
		// every save answers 403.
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ status: 'PastDue' }), [makeRule()]) }
		});

		expect(screen.getByText(/stay switched off while your subscription is not active/i)).toBeInTheDocument();
		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Disable / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Assign machines to / })).not.toBeInTheDocument();
	});

	it('withholds the custom-rule controls from a Team tenant whose subscription has lapsed', () => {
		// Custom rules are Team's, but every endpoint that touches one also carries the Pro gate, so
		// a lapsed Team tenant may not author either.
		render(AlertsPage, {
			props: {
				data: makeData(
					makeSubscription({ tier: 'Team', status: 'PastDue', alertRuleLimit: 25 }),
					[makeRule({ isCustom: true, name: 'Custom load rule' })]
				)
			}
		});

		expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'New Rule' })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'Delete rule' })).not.toBeInTheDocument();
	});

	it('keeps a Canceled subscription off the alerting controls', () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ status: 'Canceled' }), [makeRule()]) }
		});

		expect(screen.getByText(/stay switched off while your subscription is not active/i)).toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Disable / })).not.toBeInTheDocument();
	});

	it('gives Pro an enable/disable control on a built-in rule but no edit form', () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule()]) }
		});

		expect(screen.getByRole('button', { name: 'Disable Disk usage above 90%' })).toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
	});

	it('counts only custom rules against the quota so built-ins do not spend it', () => {
		const rules: AlertRuleDto[] = Array.from({ length: 8 }, (_, i) =>
			makeRule({ id: i + 1, name: `Built-in ${i + 1}` })
		);
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25, alertRuleCount: 0 }), rules)
			}
		});

		expect(screen.getByText('0 of 25 rules used')).toBeInTheDocument();
		expect(screen.queryByText('Limit reached')).not.toBeInTheDocument();
	});

	it('freezes a custom rule below Team: visible, assignments intact, and no control offered at all', () => {
		// A downgraded Team tenant keeps its custom rules and may not touch them. Every endpoint that
		// would change one refuses below Team, so the rule is shown with no control rather than a
		// control that answers 403 — and it is shown, because hiding it would make an assignment that
		// is still firing invisible to the person paying for it.
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [
					makeRule({ isCustom: true, name: 'Custom load rule', machineIds: [10, 11] })
				])
			}
		});

		expect(screen.getByText('Custom load rule')).toBeInTheDocument();
		expect(screen.getByText('Custom')).toBeInTheDocument();
		expect(screen.getByText('2 machines')).toBeInTheDocument();

		expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Disable / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Enable / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: /^Assign machines to / })).not.toBeInTheDocument();
		expect(screen.queryByRole('button', { name: 'Delete rule' })).not.toBeInTheDocument();
	});

	it('gives Team the edit form on a built-in rule', () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), [makeRule()]) }
		});

		expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument();
	});

	it('says so when a rule is watching no machines, and offers to assign some', () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule({ machineIds: [], machines: [] })]) }
		});

		expect(screen.getByText(/not watching any machines yet/i)).toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' })).toBeInTheDocument();
	});

	it('warns across the page while an enabled rule is watching nothing', () => {
		// A rule arrives unassigned and stays that way until someone acts. Saying so only in the row
		// puts the warning behind a scroll, on a screen nobody visits until an alert they expected
		// failed to arrive.
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule({ machineIds: [], machines: [] })]) }
		});

		expect(screen.getByText(/they will not fire until you assign some/i)).toBeInTheDocument();
	});

	it('counts the unwatched rules rather than claiming none of them is watching', () => {
		// "None of your alert rules are watching machines" is true only in the opening state. Once one
		// rule is assigned, a banner that still says none overstates the gap, and a banner that
		// overstates is discounted exactly like one that hides.
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [
					makeRule({ machineIds: [], machines: [] }),
					makeRule({ id: 2, name: 'CPU high', machineIds: [], machines: [] }),
					makeRule({ id: 3, name: 'Memory high', machineIds: [10] })
				])
			}
		});

		expect(screen.getByText(/2 alert rules are not watching any machines/i)).toBeInTheDocument();
		expect(screen.queryByText(/none of your alert rules/i)).not.toBeInTheDocument();
	});

	it('does not raise the page warning for a rule that is switched off', () => {
		// A disabled rule watching nothing is not a gap — it is a rule someone turned off. Warning
		// about it would train the tenant to ignore the banner that matters.
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [
					makeRule({ isEnabled: false, machineIds: [], machines: [] }),
					makeRule({ id: 2, name: 'CPU high', machineIds: [10] })
				])
			}
		});

		expect(screen.queryByText(/they will not fire until you assign some/i)).not.toBeInTheDocument();
	});

	it('opens a machine picker that saves through the rule-side assignment action', async () => {
		searchMachinesMock.mockResolvedValue(fleetPage([fleetMachine(11, 'db-01')]));
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule({ machineIds: [], machines: [] })]) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' }));

		const dialog = await screen.findByRole('dialog');
		expect(dialog).toHaveAccessibleName('Machines watched by Disk usage above 90%');
		expect(await screen.findByRole('checkbox', { name: 'Select db-01' })).not.toBeChecked();

		// Assignment has an endpoint of its own — the only one that accepts an empty set, which is
		// how a rule is parked without being switched off.
		expect(document.querySelector('form[action="?/assignRuleMachines"]')).not.toBeNull();
	});

	// The "showing the first N of M" notice that used to live here is gone with the list it
	// described: the picker now pages the whole fleet, so there is no silent remainder to warn
	// about. What replaced it — a capped bulk selection announcing itself — is covered where the
	// cap actually exists, in the picker's own tests.

	it('offers the machines the picker represented, so a save cannot remove the ones it did not', async () => {
		searchMachinesMock.mockResolvedValue(fleetPage([fleetMachine(11, 'db-01')]));
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule({ machineIds: [10] })]) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' }));
		await screen.findByRole('checkbox', { name: 'Select db-01' });
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		const offered = document.querySelector(
			'form[action="?/assignRuleMachines"] input[name="visibleMachineIds"]'
		) as HTMLInputElement;

		// What the rule already watched plus what the picker drew. Both are machines the user could
		// have unticked, and an id missing from here is carried through rather than removed.
		expect(offered.value.split(',').sort()).toEqual(['10', '11']);
	});

	it('keeps offering a machine unticked in an earlier picker session, so the removal survives a second visit', async () => {
		// The set a save may remove from is everything the user could have unticked across the whole
		// edit, not just what the last picker session happened to draw. Reopening the picker seeds it
		// from the current selection, so a machine already unticked is no longer represented — and an
		// id missing from the offered set is carried through by the server rather than removed. The
		// interface would report 1 machine while the rule went on watching 2.
		// The second session must not redraw web-01, or the list itself would re-offer it and the
		// memory this test is about would never be consulted. A filter change, another page, or a
		// machine that simply is not on the first page of a large fleet all produce this.
		searchMachinesMock
			.mockResolvedValueOnce(fleetPage([fleetMachine(10, 'web-01'), fleetMachine(11, 'db-01')]))
			.mockResolvedValue(fleetPage([fleetMachine(11, 'db-01')]));
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), [
					makeRule({
						machineIds: [10, 11],
						machines: [
							{ id: 10, name: 'web-01' },
							{ id: 11, name: 'db-01' }
						]
					})
				])
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Choose machines' }));
		await fireEvent.click(await screen.findByRole('checkbox', { name: 'Select web-01' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		// Second visit to the same picker, changing nothing.
		await fireEvent.click(screen.getByRole('button', { name: 'Choose machines' }));
		await screen.findByRole('checkbox', { name: 'Select db-01' });
		await fireEvent.click(screen.getByRole('button', { name: 'Save' }));

		const offered = document.querySelector(
			'form[action="?/updateRule"] input[name="visibleMachineIds"]'
		) as HTMLInputElement;

		expect(offered.value.split(',').sort()).toEqual(['10', '11']);
	});

	it('treats an unavailable subscription as entitled rather than as a downgrade', () => {
		render(AlertsPage, {
			props: { data: makeData(null, [makeRule()]) }
		});

		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'Disable Disk usage above 90%' })).toBeInTheDocument();
	});

	it('keeps the threshold inputs on a failed-login rule, which is counted rather than fired on', async () => {
		// The metric is measured from ingest events, but it is still a count compared against a
		// threshold over a window. Hiding those inputs, as the SSH-connection rule does, would post a
		// zero duration that the server refuses.
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), []) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'New Rule' }));
		await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'FailedSshLogin' } });

		expect(screen.getByLabelText('Operator')).toBeInTheDocument();
		expect(screen.getByLabelText('Threshold')).toBeInTheDocument();
		// Failed-login windows are evaluated on the platform's five-minute cadence, so the form must
		// not offer a window the server will refuse — and that a tighter rule would detect less with.
		expect(screen.getByLabelText('Duration (minutes)')).toHaveAttribute('min', '5');
		expect(screen.queryByText(/No threshold or duration applies/i)).not.toBeInTheDocument();
	});

	it('offers only a greater-than comparison on a failed-login count', async () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), []) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'New Rule' }));
		await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'FailedSshLogin' } });

		const operator = screen.getByLabelText('Operator') as HTMLSelectElement;
		const offered = Array.from(operator.options).map((o) => o.value);

		expect(offered).toEqual(['GreaterThan']);
		expect(operator.value).toBe('GreaterThan');
	});

	it('restores the full operator list when the metric goes back to a sampled one', async () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), []) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'New Rule' }));
		await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'FailedSshLogin' } });
		await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'CpuUsage' } });

		const operator = screen.getByLabelText('Operator') as HTMLSelectElement;

		expect(Array.from(operator.options).map((o) => o.value)).toEqual(['GreaterThan', 'LessThan', 'EqualTo']);
	});

	it('still hides the threshold inputs for a rule that fires on the event itself', async () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), []) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'New Rule' }));
		await fireEvent.change(screen.getByLabelText('Metric'), { target: { value: 'SshConnection' } });

		expect(screen.queryByLabelText('Operator')).not.toBeInTheDocument();
		expect(screen.queryByLabelText('Threshold')).not.toBeInTheDocument();
		expect(screen.getByText(/No threshold or duration applies/i)).toBeInTheDocument();
	});

	it('reports a failed-login rule as a windowed condition, not as "on event"', () => {
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [
					makeRule({ metric: 'FailedSshLogin', name: 'Failed SSH logins', threshold: 5, durationMinutes: 5 })
				])
			}
		});

		expect(screen.getByText(/GreaterThan 5/)).toBeInTheDocument();
		expect(screen.getByText('for 5m')).toBeInTheDocument();
		expect(screen.queryByText('On event')).not.toBeInTheDocument();
	});

	it('gives a failed-login rule its threshold and duration inputs when edited', async () => {
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Team', alertRuleLimit: 25 }), [
					makeRule({ metric: 'FailedSshLogin', name: 'Failed SSH logins', threshold: 5, durationMinutes: 5 })
				])
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

		expect(screen.getByLabelText('Threshold')).toHaveValue(5);
		expect(screen.getByLabelText('Duration (minutes)')).toHaveAttribute('min', '5');
		expect(screen.queryByText(/fires immediately on detection/i)).not.toBeInTheDocument();
	});

	it('leaves a self-hosted deployment fully entitled whatever its subscription row says', () => {
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Free', status: 'Canceled' }), [makeRule()], {
					selfHosted: true
				})
			}
		});

		expect(screen.queryByText(/only run on Pro and Team plans/i)).not.toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'Disable Disk usage above 90%' })).toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'New Rule' })).toBeInTheDocument();
	});
});
