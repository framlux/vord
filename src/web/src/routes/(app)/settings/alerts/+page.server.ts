// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { parsePaginationParams } from '$lib/utils/pagination';
import { canAdminTenant, canAdminMachines } from '$lib/utils/roles';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

function parseRequiredInt(formData: FormData, field: string): number | null {
	const raw = formData.get(field);
	if (raw === null) {
		return null;
	}
	const parsed = parseInt(raw as string);
	if (Number.isNaN(parsed)) {
		return null;
	}

	return parsed;
}

export const load: PageServerLoad = async ({ fetch, cookies, locals, url }) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const { page, pageSize } = parsePaginationParams(url);
	const status = url.searchParams.get('status') ?? undefined;
	const severity = url.searchParams.get('severity') ?? undefined;

	try {
		const [rules, events, webhooks, subscription] = await Promise.all([
			api.getAlertRules(),
			api.getAlertEvents({ page, pageSize, status, severity }),
			api.getWebhooks(),
			api.getSubscription().catch(() => null)
		]);

		return { rules, events, webhooks, subscription, filters: { status, severity } };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) return { rules: null, events: null, webhooks: null, subscription: null, filters: { status, severity } };
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
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

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
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

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
		if (locals.user === null || canAdminMachines(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

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
			const result = await api.createWebhook({
				name: data.get('name') as string,
				url: data.get('url') as string
			});

			return { success: true, secret: result.secret };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create webhook' });
		}
	},

	rotateSecret: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		try {
			const result = await api.rotateWebhookSecret(id);

			return { success: true, secret: result.secret };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to rotate webhook secret' });
		}
	},

	updateWebhook: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}
		const isEnabled = data.get('isEnabled') === 'on';

		try {
			await api.updateWebhook(id, { isEnabled });

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update webhook' });
		}
	},

	deleteWebhook: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

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
