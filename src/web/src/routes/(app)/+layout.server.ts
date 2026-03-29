// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url }) => {
	if (locals.user === null) {
		redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname)}`);
	}

	// If user has no tenant memberships, redirect to onboarding
	if (locals.user.tenants.length === 0) {
		redirect(302, '/onboarding');
	}

	return {
		user: locals.user
	};
};
