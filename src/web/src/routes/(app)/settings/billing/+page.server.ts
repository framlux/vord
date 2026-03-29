// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { canAdminTenant } from '$lib/utils/roles';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';
import { env } from '$env/dynamic/public';

export const load: PageServerLoad = async ({ fetch, cookies, locals }) => {
	if (!locals.user || !canAdminTenant(locals.user)) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	try {
		let subscription = null;
		try {
			subscription = await api.getSubscription();
		} catch { /* Free tier may not have a record */ }

		// Fetch billing data in parallel
		const [upcomingInvoice, invoices, usageHistory] = await Promise.all([
			api.getUpcomingInvoice().catch(() => null),
			api.getInvoices().catch(() => []),
			api.getUsageHistory(6).catch(() => [])
		]);

		return {
			subscription,
			upcomingInvoice,
			invoices,
			usageHistory,
			billingEnabled: !!env.PUBLIC_BILLING_URL
		};
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};

export const actions: Actions = {
	checkout: async ({ fetch, cookies, request }) => {
		const billingUrl = env.PUBLIC_BILLING_URL;
		if (!billingUrl) return fail(500, { message: 'Billing service not configured' });

		const formData = await request.formData();
		const tier = (formData.get('tier') as string) || 'pro';

		const authCookie = cookies.get('vord_auth');
		const tenantCookie = cookies.get('vord_tenant');

		const response = await fetch(`${billingUrl}/api/v1/checkout`, {
			method: 'POST',
			headers: {
				'Content-Type': 'application/json',
				Cookie: `vord_auth=${authCookie}; vord_tenant=${tenantCookie}`
			},
			body: JSON.stringify({ tier })
		});

		if (!response.ok) return fail(response.status, { message: 'Failed to create checkout session' });

		const data = await response.json();
		if (data.checkoutUrl) {
			redirect(303, data.checkoutUrl);
		}

		return fail(500, { message: 'No checkout URL received' });
	},

	portal: async ({ fetch, cookies }) => {
		const billingUrl = env.PUBLIC_BILLING_URL;
		if (!billingUrl) return fail(500, { message: 'Billing service not configured' });

		const authCookie = cookies.get('vord_auth');
		const tenantCookie = cookies.get('vord_tenant');

		const response = await fetch(`${billingUrl}/api/v1/portal`, {
			method: 'POST',
			headers: {
				'Content-Type': 'application/json',
				Cookie: `vord_auth=${authCookie}; vord_tenant=${tenantCookie}`
			}
		});

		if (!response.ok) return fail(response.status, { message: 'Failed to create portal session' });

		const data = await response.json();
		if (data.portalUrl) {
			redirect(303, data.portalUrl);
		}

		return fail(500, { message: 'No portal URL received' });
	},

	cancel: async ({ fetch, cookies }) => {
		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant')
		);

		try {
			const result = await api.cancelSubscription();
			return { success: result.success, message: result.message };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}
			return fail(500, { message: 'Failed to cancel subscription' });
		}
	},

	downgrade: async ({ fetch, cookies, request }) => {
		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant')
		);

		const formData = await request.formData();
		const targetTier = (formData.get('targetTier') as string) || '';

		try {
			const result = await api.downgradeSubscription(targetTier);
			return { success: result.success, message: result.message };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}
			return fail(500, { message: 'Failed to downgrade subscription' });
		}
	},

	resume: async ({ fetch, cookies }) => {
		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant')
		);

		try {
			const result = await api.resumeSubscription();
			return { success: result.success, message: result.message };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}
			return fail(500, { message: 'Failed to resume subscription' });
		}
	},

	reactivate: async ({ fetch, cookies }) => {
		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant')
		);

		try {
			const result = await api.reactivateSubscription();
			return { success: result.success, message: result.message };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}
			return fail(500, { message: 'Failed to reactivate subscription' });
		}
	}
};
