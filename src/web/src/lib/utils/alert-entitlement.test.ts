// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { canAuthorAlertRules, canManageAlertRules } from './alert-entitlement';
import type { SubscriptionDto } from '$lib/api/types';

function sub(tier: string, status: string): SubscriptionDto {
	return {
		tier,
		status,
		machineLimit: 1000,
		machineCount: 0,
		retentionDays: 30,
		currentPeriodEnd: null,
		cancelAtPeriodEnd: false,
		billingInterval: 'monthly',
		pendingAction: null,
		alertRuleLimit: 10,
		alertRuleCount: 0,
		webhookLimit: 3,
		webhookCount: 0
	};
}

describe('canManageAlertRules', () => {
	it('admits an active Pro or Team tenant', () => {
		expect(canManageAlertRules(sub('Pro', 'Active'), false)).toBe(true);
		expect(canManageAlertRules(sub('Team', 'Active'), false)).toBe(true);
	});

	it('refuses Free whatever its status', () => {
		expect(canManageAlertRules(sub('Free', 'Active'), false)).toBe(false);
		expect(canManageAlertRules(sub('Free', 'PastDue'), false)).toBe(false);
	});

	it('refuses a paid tier that is not Active, matching the server Pro gate', () => {
		expect(canManageAlertRules(sub('Pro', 'PastDue'), false)).toBe(false);
		expect(canManageAlertRules(sub('Pro', 'Canceled'), false)).toBe(false);
		expect(canManageAlertRules(sub('Team', 'PastDue'), false)).toBe(false);
		expect(canManageAlertRules(sub('Team', 'Canceled'), false)).toBe(false);
	});

	it('reads a missing subscription optimistically rather than as a downgrade', () => {
		expect(canManageAlertRules(null, false)).toBe(true);
		expect(canManageAlertRules(undefined, false)).toBe(true);
	});

	it('admits a self-hosted deployment whatever the subscription row says', () => {
		expect(canManageAlertRules(sub('Free', 'Canceled'), true)).toBe(true);
	});
});

describe('canAuthorAlertRules', () => {
	it('admits only an active Team tenant', () => {
		expect(canAuthorAlertRules(sub('Team', 'Active'), false)).toBe(true);
		expect(canAuthorAlertRules(sub('Pro', 'Active'), false)).toBe(false);
		expect(canAuthorAlertRules(sub('Free', 'Active'), false)).toBe(false);
	});

	it('refuses a lapsed Team tenant, because the Pro gate refuses it too', () => {
		expect(canAuthorAlertRules(sub('Team', 'PastDue'), false)).toBe(false);
		expect(canAuthorAlertRules(sub('Team', 'Canceled'), false)).toBe(false);
	});

	it('never admits more than canManageAlertRules does', () => {
		const tiers = ['Free', 'Pro', 'Team'];
		const statuses = ['Active', 'PastDue', 'Canceled', 'Trialing'];

		for (const tier of tiers) {
			for (const status of statuses) {
				const subscription = sub(tier, status);
				if (canAuthorAlertRules(subscription, false)) {
					expect(canManageAlertRules(subscription, false)).toBe(true);
				}
			}
		}
	});

	it('reads a missing subscription and a self-hosted deployment optimistically', () => {
		expect(canAuthorAlertRules(null, false)).toBe(true);
		expect(canAuthorAlertRules(undefined, false)).toBe(true);
		expect(canAuthorAlertRules(sub('Free', 'Canceled'), true)).toBe(true);
	});
});
