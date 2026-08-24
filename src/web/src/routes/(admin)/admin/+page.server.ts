// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient, csrfFor } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

export const load: PageServerLoad = async ({ fetch, cookies, locals }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	// An older api-server omits the field entirely; treating that as hosted is correct, because
	// the only place a version mismatch can occur is the hosted cluster mid-rollout.
	const selfHosted = locals.user?.deployment?.selfHosted === true;

	try {
		const promises: [
			Promise<Awaited<ReturnType<typeof api.getAdminUsers>>>,
			Promise<Awaited<ReturnType<typeof api.getAdminSettings>>>,
			Promise<Awaited<ReturnType<typeof api.getTenants>>>
		] = [api.getAdminUsers(), api.getAdminSettings(), api.getTenants()];

		const [users, settings, tenants] = await Promise.all(promises);

		return { users, settings, tenants, selfHosted };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};

export const actions: Actions = {
	updateSettings: async ({ fetch, cookies, request, locals }) => {
		if (locals.user === null || locals.user.isGlobalAdmin === false) {
			return fail(403, { message: 'Access denied' });
		}

		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant'),
			undefined,
			csrfFor(cookies, locals)
		);
		const data = await request.formData();
		const settingsJson = data.get('settings') as string;

		try {
			const settings = JSON.parse(settingsJson) as { key: number; value: string }[];
			await api.updateAdminSettings(settings);

			return { success: true };
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(e.status, { message: e.message });
			}

			return fail(500, { message: 'Failed to update settings' });
		}
	}
};
