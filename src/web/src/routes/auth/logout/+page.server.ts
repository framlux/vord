// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import { createServerApiClient } from '$lib/api/server';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies }) => {
	const cookie = cookies.get('vord_auth');
	if (cookie) {
		try {
			const client = createServerApiClient(fetch, cookie);
			await client.logout();
		} catch {
			// Ignore errors during logout
		}
		cookies.delete('vord_auth', { path: '/' });
	}
	redirect(302, '/auth/login');
};
