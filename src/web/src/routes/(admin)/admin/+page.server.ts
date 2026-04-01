// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';
import { env } from '$env/dynamic/public';

export const load: PageServerLoad = async ({ fetch, cookies }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const billingEnabled = !!env.PUBLIC_BILLING_URL;

	try {
		const promises: [
			Promise<Awaited<ReturnType<typeof api.getAdminUsers>>>,
			Promise<Awaited<ReturnType<typeof api.getAdminSettings>>>,
			Promise<Awaited<ReturnType<typeof api.getTenants>>>
		] = [api.getAdminUsers(), api.getAdminSettings(), api.getTenants()];

		const [users, settings, tenants] = await Promise.all(promises);

		return { users, settings, tenants, billingEnabled };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
		}
		throw e;
	}
};

export const actions: Actions = {
	updateSettings: async ({ fetch, cookies, request }) => {
		const api = createServerApiClient(
			fetch,
			cookies.get('vord_auth'),
			cookies.get('vord_tenant')
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
