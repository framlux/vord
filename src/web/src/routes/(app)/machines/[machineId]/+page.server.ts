// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import type { AlertRuleDto } from '$lib/api/types';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, params }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const id = Number(params.machineId);
	if (isNaN(id)) error(404, 'Machine not found');

	try {
		const machine = await api.getMachine(id);

		let machineDetail = null;
		try {
			machineDetail = await api.getMachineDetail(id);
		} catch (e) {
			if (e instanceof ApiError && (e.status === 404 || e.status === 403)) {
				machineDetail = null;
			} else {
				throw e;
			}
		}

		let authorizedKeys = null;
		try {
			authorizedKeys = await api.getMachineAuthorizedKeys(id);
		} catch (e) {
			if (e instanceof ApiError && (e.status === 404 || e.status === 403)) {
				authorizedKeys = null;
			} else {
				throw e;
			}
		}

		let machineAlertRules: AlertRuleDto[] = [];
		try {
			machineAlertRules = await api.getMachineAlertRules(id);
		} catch (e) {
			if (e instanceof ApiError && (e.status === 404 || e.status === 403)) {
				machineAlertRules = [];
			} else {
				throw e;
			}
		}

		let allAlertRules: AlertRuleDto[] = [];
		try {
			allAlertRules = await api.getAlertRules();
		} catch (e) {
			if (e instanceof ApiError && (e.status === 404 || e.status === 403)) {
				allAlertRules = [];
			} else {
				throw e;
			}
		}

		return { machine, machineDetail, authorizedKeys, machineAlertRules, allAlertRules };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
			if (e.status === 404) error(404, 'Machine not found');
		}
		throw e;
	}
};

export const actions: Actions = {
	updateAlertRules: async ({ fetch, cookies, request, params }) => {
		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const id = Number(params.machineId);
		if (isNaN(id)) {
			return fail(400, { message: 'Invalid machine ID' });
		}

		const formData = await request.formData();
		const ruleIds = formData.getAll('ruleIds')
			.map((v) => parseInt(v as string))
			.filter((v) => Number.isNaN(v) === false);

		try {
			await api.updateMachineAlertRules(id, ruleIds);
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}
			throw e;
		}

		return { success: true };
	}
};
