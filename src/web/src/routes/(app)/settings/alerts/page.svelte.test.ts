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

function makeData(
	subscription: SubscriptionDto | null,
	rules: AlertRuleDto[],
	overrides: { machineCount?: number; machinesTruncated?: boolean } = {}
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
		user: makeUser()
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
		expect(screen.queryByText('Limit reached')).not.toBeInTheDocument();
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
});
