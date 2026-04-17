// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, params }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const id = Number(params.machineId);
	if (isNaN(id)) error(404, 'Machine not found');

	try {
		const [machine, certificates] = await Promise.all([
			api.getMachine(id),
			api.getMachineCertificates(id)
		]);

		let machineDetail = null;
		try {
			machineDetail = await api.getMachineDetail(id);
		} catch { /* Detail may not be available yet */ }

		return { machine, certificates, machineDetail };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
			if (e.status === 404) error(404, 'Machine not found');
		}
		throw e;
	}
};
