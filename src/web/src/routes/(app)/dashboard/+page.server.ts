// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, url }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const rawPage = parseInt(url.searchParams.get('page') ?? '1', 10);
	const page = Number.isNaN(rawPage) ? 1 : Math.max(1, rawPage);
	const rawPageSize = parseInt(url.searchParams.get('pageSize') ?? '25', 10);
	const pageSize = Number.isNaN(rawPageSize) ? 25 : Math.min(100, Math.max(1, rawPageSize));
	const search = url.searchParams.get('search') ?? undefined;
	const status = url.searchParams.get('status') ?? undefined;
	const sortBy = url.searchParams.get('sortBy') ?? 'name';
	const sortDir = url.searchParams.get('sortDir') ?? 'asc';

	try {
		const fleet = await api.getFleetOverview({ page, pageSize, search, status, sortBy, sortDir });

		return { fleet, page, pageSize, search, status, sortBy, sortDir };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};
