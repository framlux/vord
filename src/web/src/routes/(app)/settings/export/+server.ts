// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { error, json, redirect } from '@sveltejs/kit';
import { canAdminTenant } from '$lib/utils/roles';
import { createServerApiClient } from '$lib/api/server';
import { ApiError } from '$lib/api/client';
import type { RequestEvent } from '@sveltejs/kit';

export const POST = async ({ fetch, cookies, locals }: RequestEvent) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	try {
		const data = await api.requestExport();

		return json(data);
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 404) error(404, 'No machine data found to export');
			error(e.status, 'Export request failed');
		}
		throw e;
	}
};

export const GET = async ({ fetch, cookies, locals, url }: RequestEvent) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const jobId = url.searchParams.get('jobId');
	if (jobId === null) {
		error(400, 'Missing jobId parameter');
	}

	if (/^\d+$/.test(jobId) === false) {
		error(400, 'Invalid jobId format');
	}

	const api = createServerApiClient(fetch, cookies.get('vord_auth'), cookies.get('vord_tenant'));

	try {
		const data = await api.getExportStatus(parseInt(jobId));

		return json(data);
	} catch (e) {
		if (e instanceof ApiError) {
			if (e.status === 401) redirect(302, '/auth/login');
			if (e.status === 404) error(404, 'Export job not found');
			error(e.status, 'Failed to get export status');
		}
		throw e;
	}
};
