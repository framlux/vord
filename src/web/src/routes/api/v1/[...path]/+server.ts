// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { dev } from '$app/environment';
import { env } from '$env/dynamic/private';
import { error, json } from '@sveltejs/kit';
import type { RequestHandler } from './$types';
import {
	mockUser,
	mockSubscription,
	mockFleetOverview,
	mockMachineList,
	mockMachineById,
	mockMachineDetailById,
	mockMachineAuthorizedKeys,
	mockFleetSshSessions,
	mockAlertRules,
	getMockMachineAlertRules
} from '$lib/api/mock-fixtures';

// SvelteKit catchall for /api/v1/* requests from client-side code (dashboard polls,
// mutations from page components, TenantSwitcher, etc.). Three modes:
//
//   1. dev + VORD_API_MOCK=true  →  serve in-memory fixtures (screenshot/demo path).
//   2. dev (no mock env var)      →  proxy to API_BASE_URL (default 127.0.0.1:12233).
//   3. production                 →  proxy to API_BASE_URL.
//
// Previously every handler was `dev ? mockGet : notFound`, which returned 404 in
// production for every browser fetch — breaking every page component that uses
// `new ApiClient('')`. The proxy form forwards cookies (vord_auth, vord_tenant) and
// the request body to the real backend, returning the response verbatim. This keeps
// the front-door origin clean (cookies stay first-party) without exposing the .NET
// service directly to browsers.

const API_BASE = env.API_BASE_URL ?? 'http://127.0.0.1:12233';

function ok<T>(data: T): Response {
	return json({ success: true, data, message: null, errors: null });
}

const mockGet: RequestHandler = async ({ params }) => {
	const path = params.path ?? '';

	if (path === 'auth/me') {
		return ok(mockUser);
	}
	if (path === 'billing/subscription') {
		return ok(mockSubscription);
	}
	if (path === 'dashboard/fleet') {
		return ok(mockFleetOverview);
	}
	if (path === 'dashboard/summary') {
		return ok({
			totalMachines: mockFleetOverview.summary.totalMachines,
			onlineMachines: mockFleetOverview.summary.onlineMachines,
			pendingApprovals: 0
		});
	}
	if (path === 'machines') {
		return ok(mockMachineList);
	}
	if (path === 'machines/ssh-sessions') {
		return ok(mockFleetSshSessions);
	}
	if (path === 'alert-rules') {
		return ok(mockAlertRules);
	}

	const machineMatch = path.match(/^machines\/(\d+)(?:\/(.+))?$/);
	if (machineMatch) {
		const id = Number(machineMatch[1]);
		const subPath = machineMatch[2];

		if (subPath === undefined) {
			const machine = mockMachineById.get(id);
			if (machine === undefined) {
				throw error(404);
			}

			return ok(machine);
		}
		if (subPath === 'detail') {
			const detail = mockMachineDetailById.get(id);
			if (detail === undefined) {
				throw error(404);
			}

			return ok(detail);
		}
		if (subPath === 'authorized-keys') {
			return ok(mockMachineAuthorizedKeys);
		}
		if (subPath === 'alert-rules') {
			return ok(getMockMachineAlertRules(id));
		}
		if (subPath === 'status') {
			const machine = mockMachineById.get(id);
			if (machine === undefined) {
				throw error(404);
			}
			const detail = mockMachineDetailById.get(id);

			return ok({
				isOnline: machine.isOnline,
				lastPing: machine.lastPing,
				commandsEnabled: machine.commandsEnabled,
				healthStatus: detail?.healthStatus ?? 0
			});
		}
	}

	throw error(404);
};

const mockPatch: RequestHandler = async ({ params, request }) => {
	const path = params.path ?? '';
	const machineMatch = path.match(/^machines\/(\d+)$/);
	if (machineMatch) {
		const id = Number(machineMatch[1]);
		const machine = mockMachineById.get(id);
		if (machine === undefined) {
			throw error(404);
		}
		const body = await request.json().catch(() => ({}));

		return ok({
			...machine,
			name: body.name ?? machine.name,
			description: body.description ?? machine.description,
			location: body.location ?? machine.location
		});
	}

	return ok({ success: true });
};

const mockMutation: RequestHandler = async () => {
	return ok({ success: true });
};

// Production / non-mock dev: proxy to the real backend, forwarding method, headers,
// cookies, and body. Strips `host` so the upstream sees its own host header.
async function proxy(event: Parameters<RequestHandler>[0]): Promise<Response> {
	const path = event.params.path ?? '';
	const search = event.url.search;
	const upstreamUrl = `${API_BASE}/api/v1/${path}${search}`;

	const upstreamHeaders = new Headers();
	for (const [k, v] of event.request.headers) {
		const lower = k.toLowerCase();
		if (lower === 'host' || lower === 'content-length') {
			continue;
		}
		upstreamHeaders.set(k, v);
	}

	// Always include the user's cookies — same-origin from the browser to SvelteKit
	// already carries them, but Node's fetch does not echo them back upstream
	// unless we explicitly include them via the Cookie header on the upstream call.
	const cookieHeader = event.request.headers.get('cookie');
	if (cookieHeader) {
		upstreamHeaders.set('cookie', cookieHeader);
	}

	const init: RequestInit = {
		method: event.request.method,
		headers: upstreamHeaders,
		redirect: 'manual'
	};
	if (event.request.method !== 'GET' && event.request.method !== 'HEAD') {
		init.body = await event.request.arrayBuffer();
	}

	const upstream = await event.fetch(upstreamUrl, init);

	// Mirror the upstream response verbatim. Set-Cookie passes through.
	const responseHeaders = new Headers(upstream.headers);

	return new Response(upstream.body, {
		status: upstream.status,
		statusText: upstream.statusText,
		headers: responseHeaders
	});
}

function isMockMode(): boolean {
	return dev && env.VORD_API_MOCK === 'true';
}

export const GET: RequestHandler = async (event) => {
	return isMockMode() ? mockGet(event) : proxy(event);
};
export const PATCH: RequestHandler = async (event) => {
	return isMockMode() ? mockPatch(event) : proxy(event);
};
export const POST: RequestHandler = async (event) => {
	return isMockMode() ? mockMutation(event) : proxy(event);
};
export const PUT: RequestHandler = async (event) => {
	return isMockMode() ? mockMutation(event) : proxy(event);
};
export const DELETE: RequestHandler = async (event) => {
	return isMockMode() ? mockMutation(event) : proxy(event);
};
