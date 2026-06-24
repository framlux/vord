// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { PageServerLoad } from './$types';

export const load: PageServerLoad = async ({ url }) => {
	let returnUrl = url.searchParams.get('returnUrl') ?? '/dashboard';

	// Prevent open redirect: only allow relative paths starting with /
	if (!returnUrl.startsWith('/') || returnUrl.startsWith('//') || returnUrl.startsWith('/\\')) {
		returnUrl = '/dashboard';
	}

	// An opaque, server-resolvable SSO slug (never a raw tenant id) may be supplied to pre-offer
	// the organization's SSO link.
	const slug = url.searchParams.get('slug') ?? null;

	return { returnUrl, slug };
};
