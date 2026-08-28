// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { error, redirect } from '@sveltejs/kit';
import { createServerApiClient } from '$lib/api/server';
import type { LayoutServerLoad } from './$types';

export const load: LayoutServerLoad = async ({ locals, url, fetch, cookies }) => {
	if (locals.user === null) {
		redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname)}`);
	}

	if (locals.user.isGlobalAdmin === false) {
		error(403, 'Access denied. Global admin privileges required.');
	}

	// The hosted product is administered from the internal operator application, so this whole
	// route group does not exist there. An absent flag means an api-server old enough to predate
	// the field, which can only be the hosted cluster mid-rollout — and such a server also
	// predates the REST gate, so its admin calls would all SUCCEED. Guessing self-hosted would
	// therefore render a fully working fleet-admin console in the hosted deployment, which is the
	// exact door being closed; the absent case has to fail closed.
	if (locals.user.deployment?.selfHosted !== true) {
		error(404, 'Not found');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const subscription = await api.getSubscription().catch(() => null);

	return {
		user: locals.user,
		subscription
	};
};
