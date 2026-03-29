// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';
import { createServerApiClient } from '$lib/api/server';

export const load: PageServerLoad = async ({ url, locals, cookies, fetch }) => {
	const token = url.searchParams.get('token');
	if (token === null) {
		redirect(302, '/dashboard');
	}

	// If not authenticated, redirect to login with returnUrl
	if (locals.user === null) {
		const returnUrl = `/invitations/accept?token=${token}`;
		redirect(302, `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`);
	}

	// Fetch invitation details
	try {
		const client = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
		const invitation = await client.getInvitationByToken(token);

		return {
			invitation,
			token,
			user: locals.user
		};
	} catch {
		return {
			invitation: null,
			token,
			user: locals.user
		};
	}
};
