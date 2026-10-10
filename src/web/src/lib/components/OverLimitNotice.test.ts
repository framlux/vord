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
		pausedMachineCount: 0,
		billableMachineCount: null,
		machineLimitIsCustom: false,
		...overrides
	};
}

describe('OverLimitNotice', () => {
	it('tells a tenant with paused hosts why, and links to the paused hosts', () => {
		render(OverLimitNotice, {
			props: { subscription: makeSubscription({ machineCount: 10, pausedMachineCount: 7, machineLimit: 3 }) }
		});

		expect(screen.getByRole('status')).toHaveTextContent(
			'7 hosts are paused: your plan keeps your 3 oldest hosts active. Remove older hosts you no longer use, or upgrade.'
		);
		expect(screen.getByRole('link', { name: 'Review paused hosts' })).toHaveAttribute(
			'href',
			'/machines?status=Paused'
		);
	});

	it('uses the singular for one paused host', () => {
		render(OverLimitNotice, {
			props: { subscription: makeSubscription({ machineCount: 4, pausedMachineCount: 1, machineLimit: 3 }) }
		});

		expect(screen.getByRole('status')).toHaveTextContent('1 host is paused: your plan keeps your 3 oldest hosts active.');
	});

	it('sends a tenant whose limit was set by agreement or support to support, without suggesting an upgrade', () => {
		render(OverLimitNotice, {
			props: {
				subscription: makeSubscription({ machineCount: 10, pausedMachineCount: 7, machineLimit: 3, machineLimitIsCustom: true })
			}
		});

		const notice = screen.getByRole('status');

		expect(notice).toHaveTextContent('Contact support to change your host limit.');
		expect(notice).not.toHaveTextContent(/upgrade/);
	});

	it('says nothing at the limit, where the plan is full but nothing is paused', () => {
		render(OverLimitNotice, { props: { subscription: makeSubscription({ machineCount: 3, pausedMachineCount: 0 }) } });

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

	it('says the plan keeps no hosts when the limit is zero, rather than "0 oldest hosts"', () => {
		render(OverLimitNotice, {
			props: { subscription: makeSubscription({ machineLimit: 0, machineCount: 2, pausedMachineCount: 2 }) }
		});

		const notice = screen.getByRole('status');

		expect(notice).toHaveTextContent('2 hosts are paused: your plan keeps no hosts active.');
		expect(notice).not.toHaveTextContent('0 oldest');
		expect(notice).not.toHaveTextContent('Remove older hosts');
		expect(notice).toHaveTextContent('Upgrade to resume them.');
	});

	it('keeps a zero-limit tenant on the contact-support line when an agreement set it', () => {
		render(OverLimitNotice, {
			props: {
				subscription: makeSubscription({ machineLimit: 0, machineCount: 2, pausedMachineCount: 2, machineLimitIsCustom: true })
			}
		});

		const notice = screen.getByRole('status');

		expect(notice).toHaveTextContent('2 hosts are paused: your plan keeps no hosts active. Contact support to change your host limit.');
		expect(notice).not.toHaveTextContent(/upgrade/i);
	});

	it('says the oldest host, not "1 oldest hosts", when the plan keeps one', () => {
		render(OverLimitNotice, {
			props: { subscription: makeSubscription({ machineLimit: 1, machineCount: 3, pausedMachineCount: 2 }) }
		});

		expect(screen.getByRole('status')).toHaveTextContent(
			'2 hosts are paused: your plan keeps your oldest host active. Remove older hosts you no longer use, or upgrade.'
		);
	});
});
