// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { createServerApiClient, csrfFor } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import { redirect, error, fail } from '@sveltejs/kit';
import type { PageServerLoad, Actions } from './$types';

function parseClusterId(raw: string): number | null {
	const id = Number(raw);

	return Number.isInteger(id) ? id : null;
}

// A request that timed out or never reached the API carries status 0, which SvelteKit refuses as a
// failure status. Anything outside the error range is reported as a server error instead.
function failureStatus(status: number): number {
	return status >= 400 && status <= 599 ? status : 500;
}

export const load: PageServerLoad = async ({ fetch, cookies, params }) => {
	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));
	const id = parseClusterId(params.clusterId);
	if (id === null) error(404, 'Cluster not found');

	try {
		const cluster = await api.getCluster(id);

		return { cluster };
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 403) error(403, 'Access denied');
			if (e.status === 404) error(404, 'Cluster not found');
		}
		throw e;
	}
};

export const actions: Actions = {
	rename: async ({ fetch, cookies, request, params, locals }) => {
		const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'), undefined, csrfFor(cookies, locals));
		const formData = await request.formData();
		const submitted = formData.get('displayName');
		const typed = typeof submitted === 'string' ? submitted : '';

		const id = parseClusterId(params.clusterId);
		if (id === null) {
			return fail(400, { message: 'Invalid cluster ID', displayName: typed });
		}

		// A blank name asks the API to drop the chosen name and go back to the derived one.
		const trimmed = typed.trim();
		const displayName = trimmed.length === 0 ? null : trimmed;

		try {
			await api.renameCluster(id, displayName);
		} catch (e) {
			if (e instanceof ApiError) {
				return fail(failureStatus(e.status), { message: e.message, displayName: typed });
			}
			throw e;
		}

		return { success: true };
	}
};
