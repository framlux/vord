// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { canAdminTenant } from '$lib/utils/roles';
import { redirect, error, fail, isRedirect } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';
import { env } from '$env/dynamic/public';

function createBillingClient(fetch: typeof globalThis.fetch, cookies: { get(name: string): string | undefined }) {
	const billingUrl = env.PUBLIC_BILLING_URL;
	if (!billingUrl) return null;

	return createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), billingUrl);
}

export const load: PageServerLoad = async ({ fetch, cookies, locals }) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	try {
		// Fetch billing data in parallel
		const [upcomingInvoice, invoices, usageHistory] = await Promise.all([
			api.getUpcomingInvoice().catch(() => null),
			api.getInvoices().catch(() => []),
			api.getUsageHistory(6).catch(() => [])
		]);

		return {
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
	checkout: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const billingApi = createBillingClient(fetch, cookies);
		if (billingApi === null) return fail(500, { message: 'Billing service not configured' });

		const formData = await request.formData();
		const tier = (formData.get('tier') as string) || 'pro';

		try {
			const data = await billingApi.createCheckoutSession(tier);
			if (data.checkoutUrl) {
				const checkoutUrlObj = new URL(data.checkoutUrl);
				if (checkoutUrlObj.hostname.endsWith('.stripe.com')) {
					redirect(303, data.checkoutUrl);
				}

				return fail(400, { message: 'Invalid checkout URL received' });
			}

			return fail(500, { message: 'No checkout URL received' });
		} catch (e) {
			if (isRedirect(e)) throw e;
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create checkout session' });
		}
	},

	portal: async ({ fetch, cookies, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const billingApi = createBillingClient(fetch, cookies);
		if (billingApi === null) return fail(500, { message: 'Billing service not configured' });

		try {
			const data = await billingApi.createPortalSession();
			if (data.portalUrl) {
				const portalUrlObj = new URL(data.portalUrl);
				if (portalUrlObj.hostname.endsWith('.stripe.com')) {
					redirect(303, data.portalUrl);
				}

				return fail(400, { message: 'Invalid portal URL received' });
			}

			return fail(500, { message: 'No portal URL received' });
		} catch (e) {
			if (isRedirect(e)) throw e;
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create portal session' });
		}
	},

	cancel: async ({ fetch, cookies, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

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

	downgrade: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

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

	resume: async ({ fetch, cookies, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

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

	reactivate: async ({ fetch, cookies, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

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
