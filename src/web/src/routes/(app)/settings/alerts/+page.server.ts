// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { parsePaginationParams } from '$lib/utils/pagination';
import { canAdminTenant } from '$lib/utils/roles';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, locals, url }) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const { page, pageSize } = parsePaginationParams(url);
	const status = url.searchParams.get('status') ?? undefined;
	const severity = url.searchParams.get('severity') ?? undefined;

	try {
		const [rules, events, webhooks] = await Promise.all([
			api.getAlertRules(),
			api.getAlertEvents({ page, pageSize, status, severity }),
			api.getWebhooks()
		]);

		return { rules, events, webhooks, filters: { status, severity } };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) return { rules: null, events: null, webhooks: null, filters: { status, severity } };
		}
		throw e;
	}
};

export const actions: Actions = {
	createRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();

		try {
			await api.createAlertRule({
				name: data.get('name') as string,
				description: (data.get('description') as string) || undefined,
				metric: data.get('metric') as string,
				operator: data.get('operator') as string,
				threshold: parseFloat(data.get('threshold') as string),
				durationMinutes: parseInt(data.get('durationMinutes') as string) || 0,
				severity: data.get('severity') as string,
				notifyEmail: data.get('notifyEmail') === 'on',
				notifyWebhook: data.get('notifyWebhook') === 'on'
			});

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create alert rule' });
		}
	},

	updateRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseInt(data.get('id') as string);

		try {
			await api.updateAlertRule(id, {
				name: data.get('name') as string,
				description: (data.get('description') as string) || undefined,
				threshold: parseFloat(data.get('threshold') as string),
				durationMinutes: parseInt(data.get('durationMinutes') as string) || 0,
				severity: data.get('severity') as string,
				isEnabled: data.get('isEnabled') === 'on',
				notifyEmail: data.get('notifyEmail') === 'on',
				notifyWebhook: data.get('notifyWebhook') === 'on'
			});

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update alert rule' });
		}
	},

	deleteRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseInt(data.get('id') as string);

		try {
			await api.deleteAlertRule(id);

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to delete alert rule' });
		}
	},

	acknowledgeEvent: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseInt(data.get('id') as string);

		try {
			await api.acknowledgeAlertEvent(id);

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to acknowledge alert event' });
		}
	},

	createWebhook: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();

		try {
			await api.createWebhook({
				name: data.get('name') as string,
				url: data.get('url') as string
			});

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create webhook' });
		}
	},

	deleteWebhook: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseInt(data.get('id') as string);

		try {
			await api.deleteWebhook(id);

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to delete webhook' });
		}
	}
};
