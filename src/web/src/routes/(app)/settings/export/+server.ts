// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { env } from '$env/dynamic/private';
import { error, json, redirect } from '@sveltejs/kit';
import { canAdminTenant } from '$lib/utils/roles';
import type { RequestEvent } from '@sveltejs/kit';

const API_BASE = env.API_BASE_URL ?? 'http://127.0.0.1:12233';

function buildCookieHeader(cookies: RequestEvent['cookies']): Record<string, string> {
	const cookieParts: string[] = [];
	const authCookie = cookies.get('vord_auth');
	const tenantCookie = cookies.get('vord_tenant');
	if (authCookie) cookieParts.push(`vord_auth=${authCookie}`);
	if (tenantCookie) cookieParts.push(`vord_tenant=${tenantCookie}`);

	return cookieParts.length > 0 ? { Cookie: cookieParts.join('; ') } : {};
}

export const POST = async ({ fetch, cookies, locals }: RequestEvent) => {
	if (locals.user === null || canAdminTenant(locals.user) === false) {
		error(403, 'Access denied');
	}

	const response = await fetch(`${API_BASE}/api/v1/tenants/export`, {
		method: 'POST',
		headers: {
			...buildCookieHeader(cookies)
		}
	});

	if (response.status === 401) {
		redirect(302, '/auth/login');
	}

	if (response.status === 404) {
		error(404, 'No machine data found to export');
	}

	if (response.ok === false) {
		error(response.status, 'Export request failed');
	}

	const data = await response.json();

	return json(data);
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

	const response = await fetch(`${API_BASE}/api/v1/tenants/export/${jobId}`, {
		headers: {
			...buildCookieHeader(cookies)
		}
	});

	if (response.status === 401) {
		redirect(302, '/auth/login');
	}

	if (response.status === 404) {
		error(404, 'Export job not found');
	}

	if (response.ok === false) {
		error(response.status, 'Failed to get export status');
	}

	const data = await response.json();

	return json(data);
};
