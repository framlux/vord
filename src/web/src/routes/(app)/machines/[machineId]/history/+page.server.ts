// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, params, parent }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const id = Number(params.machineId);
	if (isNaN(id)) error(404, 'Machine not found');

	const parentData = await parent();
	const retentionDays = parentData.subscription?.retentionDays ?? 1;

	// Determine default range based on tenant retention
	let defaultRange = '24h';
	if (retentionDays < 1) {
		defaultRange = '1h';
	} else if (retentionDays < 7) {
		defaultRange = '24h';
	}

	try {
		const machine = await api.getMachine(id);

		let cpuHistory = null;
		try {
			cpuHistory = await api.getMachineCpuHistory(id, defaultRange);
		} catch { /* History may not be available yet */ }

		return { machine, cpuHistory, defaultRange, retentionDays };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
			if (e.status === 404) error(404, 'Machine not found');
		}
		throw e;
	}
};
