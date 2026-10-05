// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { SubscriptionDto, UserDto } from '$lib/api/types';
import { UNLIMITED_LIMIT } from '$lib/utils/tier';
import SettingsPage from './+page.svelte';

function makeSubscription(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return {
		tier: 'Pro',
		status: 'Active',
		machineLimit: 1000,
		machineCount: 3,
		retentionDays: 60,
		currentPeriodEnd: '2027-03-01T00:00:00Z',
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

function makeData(subscription: SubscriptionDto | null) {
	return {
		user: makeUser(),
		tenants: [{ id: 1, name: 'Acme Corp', logoUrl: '', isActive: true }],
		subscription
	};
}

describe('settings page subscription card', () => {
	it('labels an Enterprise date as the end of the term', () => {
		render(SettingsPage, {
			props: { data: makeData(makeSubscription({ tier: 'Enterprise', billingInterval: null })) }
		});

		expect(screen.getByText('Term ends')).toBeInTheDocument();
		expect(screen.queryByText('Current Period Ends')).not.toBeInTheDocument();
	});

	it('keeps the billing-period label for a Stripe-billed tier', () => {
		render(SettingsPage, { props: { data: makeData(makeSubscription({ tier: 'Team' })) } });

		expect(screen.getByText('Current Period Ends')).toBeInTheDocument();
		expect(screen.queryByText('Term ends')).not.toBeInTheDocument();
	});

	it('reports an unlimited machine limit as Unlimited instead of the number the server sends', () => {
		render(SettingsPage, {
			props: {
				data: makeData(
					makeSubscription({ tier: 'Enterprise', machineLimit: UNLIMITED_LIMIT, machineCount: 7 })
				)
			}
		});

		expect(screen.getByText(/7\s*\/ Unlimited/)).toBeInTheDocument();
		expect(screen.queryByText(/2147483647/)).not.toBeInTheDocument();
	});

	it('reports a finite machine limit against its usage', () => {
		render(SettingsPage, {
			props: { data: makeData(makeSubscription({ machineLimit: 50, machineCount: 7 })) }
		});

		expect(screen.getByText(/7\s*\/ 50/)).toBeInTheDocument();
	});
});
