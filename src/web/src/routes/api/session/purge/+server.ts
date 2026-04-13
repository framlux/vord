// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { purgeSession } from '../../../../hooks.server';
import type { RequestHandler } from './$types';

export const POST: RequestHandler = async ({ cookies }) => {
	const authCookie = cookies.get('vord_auth');
	const tenantCookie = cookies.get('vord_tenant');
	if (authCookie) {
		purgeSession(authCookie, tenantCookie);
	}

	return new Response(null, { status: 204 });
};
