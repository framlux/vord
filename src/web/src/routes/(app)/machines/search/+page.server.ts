// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import type { MachineSearchParams } from '$lib/api/types';

export const load: PageServerLoad = async ({ fetch, cookies, url }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const rawPage = Number(url.searchParams.get('page'));
	const page = Number.isNaN(rawPage) || rawPage < 1 ? 1 : rawPage;

	const params: MachineSearchParams = {
		page,
		pageSize: 25,
		search: url.searchParams.get('search') || undefined,
		healthStatus: url.searchParams.get('healthStatus') || undefined,
		os: url.searchParams.get('os') || undefined,
		type: url.searchParams.get('type') || undefined,
		sortBy: url.searchParams.get('sortBy') || undefined,
		sortDir: url.searchParams.get('sortDir') || undefined
	};

	// Numeric range filters
	const numParam = (key: string): number | undefined => {
		const raw = url.searchParams.get(key);
		if (raw === null) return undefined;
		const n = Number(raw);

		return Number.isNaN(n) ? undefined : n;
	};

	params.cpuMin = numParam('cpuMin');
	params.cpuMax = numParam('cpuMax');
	params.memoryMin = numParam('memoryMin');
	params.memoryMax = numParam('memoryMax');
	params.diskMin = numParam('diskMin');
	params.diskMax = numParam('diskMax');
	params.pendingUpdatesMin = numParam('pendingUpdatesMin');
	params.securityUpdatesMin = numParam('securityUpdatesMin');
	params.failedServicesMin = numParam('failedServicesMin');

	// Boolean filters
	if (url.searchParams.get('hasDiskHealthIssue') === 'true') params.hasDiskHealthIssue = true;
	if (url.searchParams.get('hasHardwareIssue') === 'true') params.hasHardwareIssue = true;

	// Date filters
	params.lastSeenAfter = url.searchParams.get('lastSeenAfter') || undefined;
	params.lastSeenBefore = url.searchParams.get('lastSeenBefore') || undefined;

	try {
		const results = await api.searchMachines(params);

		return { results, filters: params };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};
