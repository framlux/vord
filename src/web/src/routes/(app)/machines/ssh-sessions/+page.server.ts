// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { parsePaginationParams } from '$lib/utils/pagination';
import { canAdminMachines } from '$lib/utils/roles';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, locals, url }) => {
	if (locals.user === null || canAdminMachines(locals.user) === false) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const { page, pageSize } = parsePaginationParams(url, { pageSize: 50 });
	const search = url.searchParams.get('search') ?? undefined;

	try {
		const sessions = await api.getFleetSshSessions({ page, pageSize, search });

		return { sessions, search: search ?? '' };
	} catch (e) {
		if (e instanceof ApiError && e.status === 401) {
			redirect(302, '/auth/login');
		}
		throw e;
	}
};
