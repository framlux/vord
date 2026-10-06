// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { InvoiceDto, SubscriptionDto, UpcomingInvoiceDto, UserDto } from '$lib/api/types';
import { UNLIMITED_LIMIT } from '$lib/utils/tier';
import BillingPage from './+page.svelte';

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

function makeEnterprise(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
	return makeSubscription({
		tier: 'Enterprise',
		billingInterval: null,
		machineLimit: 5000,
		retentionDays: 365,
		...overrides
	});
}

function makeUpcomingInvoice(): UpcomingInvoiceDto {
	return {
		hasInvoice: true,
		amountDueCents: 4200,
		currency: 'usd',
		periodStart: null,
		periodEnd: null,
		nextPaymentAttempt: null,
		unitAmountCents: 1400,
		discountAmountCents: 0,
		lines: []
	};
}

function makeInvoice(): InvoiceDto {
	return {
		id: 'in_1',
		created: '2026-09-01T00:00:00Z',
		periodStart: null,
		periodEnd: null,
		amountCents: 4200,
		currency: 'usd',
		status: 'paid',
		hostedInvoiceUrl: '',
		invoicePdfUrl: ''
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

function makeData(subscription: SubscriptionDto | null, overrides: Record<string, unknown> = {}) {
	return {
		user: makeUser(),
		subscription,
		upcomingInvoice: null,
		invoices: [],
		usageHistory: [],
		catalog: [],
		billingServiceConfigured: true,
		...overrides
	};
}

describe('billing page for an Enterprise tenant', () => {
	it('shows the agreement and offers no Stripe action', () => {
		const { container } = render(BillingPage, {
			props: { data: makeData(makeEnterprise()), form: null }
		});

		expect(screen.getByRole('heading', { name: 'Enterprise agreement' })).toBeInTheDocument();
		expect(screen.getByRole('link', { name: 'Contact us about your agreement' })).toHaveAttribute(
			'href',
			'mailto:vord@framlux.io?subject=Enterprise%20agreement'
		);

		// Every Stripe-backed action on this page is a form posting to a named action.
		expect(container.querySelectorAll('form')).toHaveLength(0);
		expect(screen.queryByText('Manage Subscription')).not.toBeInTheDocument();
		expect(screen.queryByText('Cancel Account')).not.toBeInTheDocument();
		expect(screen.queryByText(/Downgrade to/)).not.toBeInTheDocument();
		expect(screen.queryByText(/Upgrade to/)).not.toBeInTheDocument();
	});

	it('labels the date as the end of the term and shows member usage against the agreed limit', () => {
		render(BillingPage, {
			props: { data: makeData(makeEnterprise({ memberLimit: 40, memberCount: 12 })), form: null }
		});

		expect(screen.getByText('Term ends')).toBeInTheDocument();
		expect(screen.queryByText('Current Period Ends')).not.toBeInTheDocument();
		expect(screen.getByText('Member Usage')).toBeInTheDocument();
		expect(screen.getByText('12 / 40')).toBeInTheDocument();
	});

	it('reports an unlimited member limit as Unlimited', () => {
		render(BillingPage, {
			props: {
				data: makeData(makeEnterprise({ memberLimit: UNLIMITED_LIMIT, memberCount: 12 })),
				form: null
			}
		});

		expect(screen.getByText('Unlimited')).toBeInTheDocument();
		expect(screen.getByText('3 / 5000')).toBeInTheDocument();
		expect(screen.queryByText(/2147483647/)).not.toBeInTheDocument();
	});

	it('reports an unlimited machine limit as Unlimited, without a percentage bar', () => {
		const { container } = render(BillingPage, {
			props: {
				data: makeData(makeEnterprise({ machineLimit: UNLIMITED_LIMIT, machineCount: 120 })),
				form: null
			}
		});

		expect(screen.getByText('Unlimited')).toBeInTheDocument();
		expect(screen.queryByText(/2147483647/)).not.toBeInTheDocument();
		expect(screen.queryByText(/120 \//)).not.toBeInTheDocument();
		expect(container.querySelector('.bg-red-500, .bg-amber-500')).toBeNull();
	});

	it('reports finite limits against their usage', () => {
		const { container } = render(BillingPage, {
			props: {
				data: makeData(makeEnterprise({ machineLimit: 100, machineCount: 95 })),
				form: null
			}
		});

		expect(screen.getByText('95 / 100')).toBeInTheDocument();
		expect(container.querySelector('.bg-red-500')).not.toBeNull();
	});

	it('drops the Stripe-priced sections', () => {
		render(BillingPage, {
			props: {
				data: makeData(makeEnterprise(), {
					upcomingInvoice: makeUpcomingInvoice(),
					invoices: [makeInvoice()]
				}),
				form: null
			}
		});

		expect(screen.queryByText('Current Period')).not.toBeInTheDocument();
		expect(screen.queryByText('Invoice History')).not.toBeInTheDocument();
		expect(screen.queryByText('Plan Comparison')).not.toBeInTheDocument();
		expect(screen.queryByText('Cost Calculator')).not.toBeInTheDocument();
	});

	it('offers no payment, reactivation or undo action when the subscription is past due, canceled or pending a change', () => {
		const { container } = render(BillingPage, {
			props: {
				data: makeData(
					makeEnterprise({ status: 'PastDue', cancelAtPeriodEnd: true, pendingAction: 'CancelAccount' })
				),
				form: null
			}
		});

		expect(container.querySelectorAll('form')).toHaveLength(0);
		expect(screen.queryByText('Payment Past Due')).not.toBeInTheDocument();
		expect(screen.queryByText('Undo Cancellation')).not.toBeInTheDocument();
	});

	it('offers no reactivation checkout once the agreement is canceled', () => {
		const { container } = render(BillingPage, {
			props: { data: makeData(makeEnterprise({ status: 'Canceled' })), form: null }
		});

		expect(container.querySelectorAll('form')).toHaveLength(0);
		expect(screen.queryByText(/Reactivate with/)).not.toBeInTheDocument();
	});
});

describe('billing page for a Stripe-billed tenant', () => {
	it('keeps the subscription actions and pricing sections for Team', () => {
		render(BillingPage, {
			props: {
				data: makeData(makeSubscription({ tier: 'Team' }), {
					upcomingInvoice: makeUpcomingInvoice(),
					invoices: [makeInvoice()]
				}),
				form: null
			}
		});

		expect(screen.queryByRole('heading', { name: 'Enterprise agreement' })).not.toBeInTheDocument();
		expect(screen.getByRole('button', { name: /Manage Subscription/ })).toBeInTheDocument();
		expect(screen.getByText('Downgrade to Pro')).toBeInTheDocument();
		expect(screen.getByText('Current Period Ends')).toBeInTheDocument();
		expect(screen.getByText('Current Period')).toBeInTheDocument();
		expect(screen.getByText('Invoice History')).toBeInTheDocument();
		expect(screen.getByText('Plan Comparison')).toBeInTheDocument();
		expect(screen.getByText('Cost Calculator')).toBeInTheDocument();
		expect(screen.queryByText('Member Usage')).not.toBeInTheDocument();
	});

	it('keeps the upgrade funnel for Free', () => {
		render(BillingPage, {
			props: { data: makeData(makeSubscription({ tier: 'Free', billingInterval: null })), form: null }
		});

		expect(screen.getByText('Upgrade to Pro')).toBeInTheDocument();
		expect(screen.getByText('Upgrade to Team')).toBeInTheDocument();
	});
});

describe('billing page tells a downgrading or canceling customer what happens to their hosts', () => {
	it('says a downgrade to Free keeps existing hosts and blocks new ones at the limit', async () => {
		render(BillingPage, { props: { data: makeData(makeSubscription()), form: null } });

		await fireEvent.click(screen.getByRole('button', { name: /Downgrade to Free/ }));

		expect(
			screen.getByText(
				'Machine limit drops to 3. Existing hosts are not removed, but new hosts cannot be added while you have 3 or more.'
			)
		).toBeInTheDocument();
		expect(screen.queryByText('Machine limit will be reduced to 3')).not.toBeInTheDocument();
	});

	it('says cancellation moves the account to Free instead of ending all service', async () => {
		render(BillingPage, { props: { data: makeData(makeSubscription()), form: null } });

		await fireEvent.click(screen.getByRole('button', { name: 'Cancel Account' }));

		expect(screen.getByText(/your account moves to the Free plan/)).toBeInTheDocument();
		expect(screen.getByText(/Hosts are not removed/)).toBeInTheDocument();
		expect(screen.getByText(/uninstall the agent/)).toBeInTheDocument();
		expect(screen.queryByText(/lose ALL service/)).not.toBeInTheDocument();
		expect(screen.queryByText(/stops all service entirely/)).not.toBeInTheDocument();
	});

	it('describes a pending cancellation as a move to Free', () => {
		render(BillingPage, {
			props: { data: makeData(makeSubscription({ pendingAction: 'CancelAccount', cancelAtPeriodEnd: true })), form: null }
		});

		expect(screen.getByText(/Your paid plan will end and your account will move to the Free plan/)).toBeInTheDocument();
		expect(screen.queryByText('Your account will be canceled')).not.toBeInTheDocument();
	});
});
