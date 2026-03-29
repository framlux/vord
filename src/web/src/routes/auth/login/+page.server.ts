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

	const tenant = url.searchParams.get('tenant') ?? null;

	return { returnUrl, tenant };
};
