// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import {
	deriveBillingPageState,
	billingIntervalLabel,
	findCatalogPrice,
	monthlyEquivalentCents
} from './billing-state';
import type { SubscriptionDto, CatalogItemDto } from '$lib/api/types';

function makeSub(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return {
		tier: 'Pro',
		status: 'Active',
		machineLimit: 1000,
		machineCount: 3,
		retentionDays: 30,
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

describe('deriveBillingPageState', () => {
	it('returns free when there is no subscription record', () => {
		expect(deriveBillingPageState(null)).toBe('free');
	});

	it('returns free for an active Free-tier subscription', () => {
		expect(deriveBillingPageState(makeSub({ tier: 'Free', billingInterval: null }))).toBe('free');
	});

	it('returns active for an active paid subscription', () => {
		expect(deriveBillingPageState(makeSub())).toBe('active');
	});

	it('returns pending-change when cancel-at-period-end is set', () => {
		expect(deriveBillingPageState(makeSub({ cancelAtPeriodEnd: true }))).toBe('pending-change');
	});

	it('returns past-due for a past-due subscription', () => {
		expect(deriveBillingPageState(makeSub({ status: 'PastDue' }))).toBe('past-due');
	});

	it('canceled wins over cancel-at-period-end and past-due', () => {
		expect(
			deriveBillingPageState(makeSub({ status: 'Canceled', cancelAtPeriodEnd: true }))
		).toBe('canceled');
	});

	it('pending-change wins over past-due', () => {
		expect(
			deriveBillingPageState(makeSub({ status: 'PastDue', cancelAtPeriodEnd: true }))
		).toBe('pending-change');
	});
});

describe('billingIntervalLabel', () => {
	it('maps monthly to Monthly', () => {
		expect(billingIntervalLabel('monthly')).toBe('Monthly');
	});

	it('maps annual to Annual', () => {
		expect(billingIntervalLabel('annual')).toBe('Annual');
	});

	it('returns null for null', () => {
		expect(billingIntervalLabel(null)).toBeNull();
	});

	it('returns null for unknown values', () => {
		expect(billingIntervalLabel('weekly')).toBeNull();
	});
});

const catalog: CatalogItemDto[] = [
	{ tier: 'Pro', interval: 'monthly', unitAmountCents: 300, currency: 'usd', isMetered: true },
	{ tier: 'Pro', interval: 'annual', unitAmountCents: 3000, currency: 'usd', isMetered: true },
	{ tier: 'Team', interval: 'monthly', unitAmountCents: 500, currency: 'usd', isMetered: true }
];

describe('findCatalogPrice', () => {
	it('finds a matching tier and interval', () => {
		expect(findCatalogPrice(catalog, 'Pro', 'annual')?.unitAmountCents).toBe(3000);
	});

	it('returns null when there is no match', () => {
		expect(findCatalogPrice(catalog, 'Team', 'annual')).toBeNull();
	});

	it('returns null for an empty catalog', () => {
		expect(findCatalogPrice([], 'Pro', 'monthly')).toBeNull();
	});
});

describe('monthlyEquivalentCents', () => {
	it('returns the unit amount for monthly prices', () => {
		expect(monthlyEquivalentCents(catalog[0])).toBe(300);
	});

	it('divides annual prices by twelve, rounded', () => {
		expect(monthlyEquivalentCents(catalog[1])).toBe(250);
	});
});
