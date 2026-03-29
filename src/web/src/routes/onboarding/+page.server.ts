// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ locals }) => {
	if (locals.user === null) {
		redirect(302, `/auth/login?returnUrl=${encodeURIComponent('/onboarding')}`);
	}

	// If user already has tenants, redirect to dashboard
	if (locals.user.tenants && locals.user.tenants.length > 0) {
		redirect(302, '/dashboard');
	}

	return { user: locals.user };
};
