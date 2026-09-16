// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { AlertRuleDto, SubscriptionDto, UserDto } from '$lib/api/types';

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

import AlertsPage from './+page.svelte';

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
	overrides: { machineCount?: number; machinesTruncated?: boolean; selfHosted?: boolean } = {}
) {
	const machines = [
		{ id: 10, name: 'web-01' },
		{ id: 11, name: 'db-01' }
	];

	return {
		rules,
		events: null,
		integrations: null,
		providers: null,
		subscription,
		machines,
		machineCount: overrides.machineCount ?? machines.length,
		machinesTruncated: overrides.machinesTruncated ?? false,
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

	it('opens a machine picker posting to the rule-side assignment action', async () => {
		render(AlertsPage, {
			props: { data: makeData(makeSubscription(), [makeRule({ machineIds: [], machines: [] })]) }
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' }));

		const group = screen.getByRole('group', { name: 'Machines watched by Disk usage above 90%' });
		expect(group).toBeInTheDocument();
		expect(group.closest('form')?.getAttribute('action')).toBe('?/assignRuleMachines');
		expect(screen.getByLabelText('db-01')).not.toBeChecked();
	});

	it('says which machines the picker is showing when the fleet does not fit on one page', async () => {
		// A rule can report 150 machines above a list of 100 checkboxes. Left unsaid, the tenant reads
		// the list as the whole fleet and the save as having unchecked the rest.
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [makeRule()], { machineCount: 150, machinesTruncated: true })
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' }));

		expect(screen.getAllByText(/Showing the first 2 of 150 machines/i).length).toBeGreaterThan(0);
	});

	it('sends the machines the picker offered, so the save cannot remove the ones it did not', async () => {
		render(AlertsPage, {
			props: {
				data: makeData(makeSubscription(), [makeRule()], { machineCount: 150, machinesTruncated: true })
			}
		});

		await fireEvent.click(screen.getByRole('button', { name: 'Assign machines to Disk usage above 90%' }));

		const group = screen.getByRole('group', { name: 'Machines watched by Disk usage above 90%' });
		const offered = group.closest('form')?.querySelector('input[name="visibleMachineIds"]');

		expect(offered).toHaveValue('10,11');
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
