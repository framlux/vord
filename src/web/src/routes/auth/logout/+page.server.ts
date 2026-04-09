// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import { createServerApiClient } from '$lib/api/server';
import { purgeSession } from '../../../hooks.server';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies }) => {
	const authCookie = cookies.get('vord_auth');
	const tenantCookie = cookies.get('vord_tenant');

	if (authCookie) {
		try {
			const client = createServerApiClient(fetch, authCookie, tenantCookie);
			await client.logout();
		} catch {
			// Ignore errors during logout
		}

		purgeSession(authCookie, tenantCookie);
		cookies.delete('vord_auth', { path: '/' });
	}

	if (tenantCookie) {
		cookies.delete('vord_tenant', { path: '/' });
	}

	redirect(302, '/auth/login');
};
