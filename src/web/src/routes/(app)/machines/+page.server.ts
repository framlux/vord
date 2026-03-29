// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, url }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const rawPage = Number(url.searchParams.get('page'));
	const page = Number.isNaN(rawPage) || rawPage < 1 ? 1 : rawPage;
	const search = url.searchParams.get('search') || undefined;
	const os = url.searchParams.get('os') || undefined;
	const type = url.searchParams.get('type') || undefined;
	const status = url.searchParams.get('status') || undefined;
	const sortBy = url.searchParams.get('sortBy') || undefined;
	const sortDir = url.searchParams.get('sortDir') || undefined;

	try {
		const machines = await api.getMachines({ page, pageSize: 25, search, os, type, status, sortBy, sortDir });
		return { machines, filters: { search: search ?? '', os: os ?? '', type: type ?? '', status: status ?? '' } };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};
