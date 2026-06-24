// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import { createServerApiClient, csrfFor } from '$lib/api/server';
import { purgeSession } from '../../../hooks.server';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, locals }) => {
	const authCookie = cookies.get('vord_auth');
	const tenantCookie = cookies.get('vord_tenant');

	if (authCookie) {
		try {
			// Logout is a POST and thus passes through the JSON CSRF gate; forward the
			// double-submit pair so the backend can revoke the server-side session.
			const client = createServerApiClient(fetch, authCookie, tenantCookie, undefined, csrfFor(cookies, locals));
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
