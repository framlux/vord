// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient, csrfFor } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { parsePaginationParams } from '$lib/utils/pagination';
import { canAdminTenant, canAdminMachines } from '$lib/utils/roles';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

// The machine list endpoint clamps a page to 100. Asking for more is not an error there, it is a
// silent truncation, so the page asks for exactly what it can be given and says so when the fleet
// does not fit.
const MACHINE_PAGE_SIZE = 100;

function parseMachineIds(formData: FormData, field: string): number[] {
	return formData
		.getAll(field)
		.flatMap((v) => (v as string).split(','))
		.map((v) => parseInt(v.trim()))
		.filter((v) => Number.isNaN(v) === false);
}

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
		const [rules, events, integrations, providers, subscription, machinesResponse] = await Promise.all([
			// Alert rules answer for every tier — a Free tenant sees its built-in rules disabled, which
			// is the upgrade case — so a failure there is a real failure and must reach the catch.
			// The three below are still Pro-gated, and a 403 from any of them would otherwise take the
			// whole page down for a Free tenant.
			api.getAlertRules(),
			api.getAlertEvents({ page, pageSize, status, severity }).catch(() => null),
			api.getIntegrations().catch(() => null),
			api.getIntegrationProviders().catch(() => null),
			api.getSubscription().catch(() => null),
			api.getMachines({ pageSize: MACHINE_PAGE_SIZE })
		]);

		const machines = machinesResponse.items.map((m) => ({ id: m.id, name: m.name }));

		// The rule row counts a rule's machines from the rule itself, so it can report more machines
		// than the picker below it can draw. Saying which is which is the difference between a
		// confusing page and a misleading one.
		const machineCount = machinesResponse.totalCount;
		const machinesTruncated = machineCount > machines.length;

		return {
			rules,
			events,
			integrations,
			providers,
			subscription,
			machines,
			machineCount,
			machinesTruncated,
			filters: { status, severity }
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
	createRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();

		const machineIds = parseMachineIds(data, 'machineIds');

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
				notifyWebhook: data.get('notifyWebhook') === 'on',
				machineIds
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

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		// The API may only unassign machines the form says it was choosing from, so the offered set
		// travels with the request. Without it a truncated picker would delete the assignments it
		// never rendered.
		const machineIds = parseMachineIds(data, 'machineIds');
		const visibleMachineIds = parseMachineIds(data, 'visibleMachineIds');

		try {
			await api.updateAlertRule(id, {
				name: data.get('name') as string,
				description: (data.get('description') as string) || undefined,
				metric: data.get('metric') as string,
				threshold: parseFloat(data.get('threshold') as string),
				durationMinutes: parseInt(data.get('durationMinutes') as string) || 0,
				severity: data.get('severity') as string,
				isEnabled: data.get('isEnabled') === 'on',
				notifyEmail: data.get('notifyEmail') === 'on',
				notifyWebhook: data.get('notifyWebhook') === 'on',
				machineIds,
				visibleMachineIds
			});

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update alert rule' });
		}
	},

	toggleRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		try {
			await api.setAlertRuleEnabled(id, { isEnabled: data.get('isEnabled') === 'on' });

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update alert rule' });
		}
	},

	assignRuleMachines: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		// An empty selection is meaningful here and is forwarded as such: it parks the rule so it
		// watches nothing, without turning it off. The rule update endpoint rejects that, which is why
		// assignment has an endpoint of its own.
		const machineIds = parseMachineIds(data, 'machineIds');

		// The offered set bounds what the API is allowed to remove. A picker that could only draw the
		// first page of the fleet must not be read as having unchecked the rest.
		const visibleMachineIds = parseMachineIds(data, 'visibleMachineIds');

		try {
			await api.updateAlertRuleMachines(id, { machineIds, visibleMachineIds });

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update alert rule machines' });
		}
	},

	deleteRule: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
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

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
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

	createIntegration: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();

		const provider = data.get('provider') as string;
		const name = (data.get('name') as string) || undefined;

		// Collect configuration fields (prefixed with "config.")
		const configuration: Record<string, string> = {};
		for (const [key, value] of data.entries()) {
			if (key.startsWith('config.')) {
				configuration[key.substring(7)] = value as string;
			}
		}

		try {
			const result = await api.createIntegration({ provider, name, configuration });

			return { success: true, secret: result.secret };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to create integration' });
		}
	},

	updateIntegration: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		const name = (data.get('name') as string) || undefined;
		const isEnabled = data.get('isEnabled') === 'on';

		try {
			await api.updateIntegration(id, { name, isEnabled });

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update integration' });
		}
	},

	deleteIntegration: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		try {
			await api.deleteIntegration(id);

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to delete integration' });
		}
	},

	rotateSecret: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		try {
			const result = await api.rotateIntegrationSecret(id);

			return { success: true, secret: result.secret };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to rotate integration secret' });
		}
	},

	testIntegration: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || canAdminTenant(locals.user) === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const data = await request.formData();
		const id = parseRequiredInt(data, 'id');
		if (id === null) {
			return fail(400, { message: 'Invalid ID' });
		}

		try {
			const result = await api.testIntegration(id);

			return { success: true, testResult: result };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to test integration' });
		}
	}
};
