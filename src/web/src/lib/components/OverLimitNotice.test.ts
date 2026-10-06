// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { SubscriptionDto } from '$lib/api/types';
import { UNLIMITED_LIMIT } from '$lib/utils/tier';
import OverLimitNotice from './OverLimitNotice.svelte';

function makeSubscription(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return {
		tier: 'Free',
		status: 'Active',
		machineLimit: 3,
		machineCount: 3,
		retentionDays: 1,
		currentPeriodEnd: null,
		cancelAtPeriodEnd: false,
		billingInterval: null,
		pendingAction: null,
		alertRuleLimit: 0,
		alertRuleCount: 0,
		webhookLimit: 0,
		webhookCount: 0,
		memberLimit: 1,
		memberCount: 1,
		...overrides
	};
}

describe('OverLimitNotice', () => {
	it('tells a tenant over its limit why it cannot add hosts, and links to the machine list', () => {
		render(OverLimitNotice, { props: { subscription: makeSubscription({ machineCount: 10 }) } });

		const notice = screen.getByRole('status');
		expect(notice).toHaveTextContent(
			"10 hosts on a 3-host plan. New hosts can't be added until you have fewer than 3, or upgrade."
		);
		expect(screen.getByRole('link', { name: 'Review hosts' })).toHaveAttribute('href', '/machines');
	});

	it('says nothing at the limit, where the plan is full but not exceeded', () => {
		render(OverLimitNotice, { props: { subscription: makeSubscription({ machineCount: 3 }) } });

		expect(screen.queryByRole('status')).not.toBeInTheDocument();
	});

	it('says nothing on an unlimited plan however many hosts there are', () => {
		render(OverLimitNotice, {
			props: { subscription: makeSubscription({ machineLimit: UNLIMITED_LIMIT, machineCount: 50000 }) }
		});

		expect(screen.queryByRole('status')).not.toBeInTheDocument();
	});

	it('says nothing when there is no subscription to compare against', () => {
		render(OverLimitNotice, { props: { subscription: null } });

		expect(screen.queryByRole('status')).not.toBeInTheDocument();
	});

	it('treats a limit of zero as none allowed, not as unlimited', () => {
		render(OverLimitNotice, { props: { subscription: makeSubscription({ machineLimit: 0, machineCount: 2 }) } });

		expect(screen.getByRole('status')).toHaveTextContent('2 hosts on a 0-host plan.');
	});

	it('uses the singular for one host', () => {
		render(OverLimitNotice, { props: { subscription: makeSubscription({ machineLimit: 0, machineCount: 1 }) } });

		expect(screen.getByRole('status')).toHaveTextContent('1 host on a 0-host plan.');
	});
});
