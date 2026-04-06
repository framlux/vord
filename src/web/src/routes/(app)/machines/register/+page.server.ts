// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { createServerApiClient } from '$lib/api/server';
import { canAdminMachines } from '$lib/utils/roles';

export const load: PageServerLoad = async ({ locals, cookies, fetch }) => {
	if (!locals.user || !canAdminMachines(locals.user)) {
		redirect(302, '/dashboard');
	}

	const client = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const tokens = await client.getRegistrationTokens();

	return {
		tokens,
		user: locals.user
	};
};
