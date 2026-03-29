// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, locals, url }) => {
	if (locals.user === null) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	const page = parseInt(url.searchParams.get('page') ?? '1');
	const pageSize = parseInt(url.searchParams.get('pageSize') ?? '50');
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
