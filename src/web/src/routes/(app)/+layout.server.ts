// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import { createServerApiClient } from '$lib/api/server';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url, fetch, cookies }) => {
	if (locals.user === null) {
		redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname)}`);
	}

	// If user has no tenant memberships, redirect to onboarding
	if (locals.user.tenants.length === 0) {
		redirect(302, '/onboarding');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const subscription = await api.getSubscription().catch(() => null);

	return {
		user: locals.user,
		subscription
	};
};
