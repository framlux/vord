// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { dev } from '$app/environment';
import { env } from '$env/dynamic/private';
import { error, json } from '@sveltejs/kit';
import { API_BASE } from '$lib/api/server';
import { exceedsPageSizeCeiling, matchFleetMachines, pageOfFleet, type MockFleetFilter } from '$lib/api/mock-fleet-query';
import type { RequestHandler } from './$types';
import {
	mockUser,
	mockSubscription,
	mockFleetOverview,
	mockFleetMachines,
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

function ok<T>(data: T): Response {
	return json({ success: true, data, message: null, errors: null });
}

// The list and the bulk selection read the same parameters, so they are read in one place.
function fleetFilterFrom(url: URL): MockFleetFilter {
	return {
		search: url.searchParams.get('search') ?? undefined,
		healthStatus: url.searchParams.get('healthStatus') ?? undefined
	};
}

const mockGet: RequestHandler = async ({ params, url }) => {
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
	// The assignment picker reaches the fleet through search rather than the machine list, so
	// without these two mock mode renders a picker that answers 404 to everything. Both answer
	// through one filter, as production does, so select-all resolves exactly what the list shows.
	// Paging is applied for real: a picker that pages is only exercised by a source that pages.
	if (path === 'machines/search') {
		const pageSize = Number(url.searchParams.get('pageSize') ?? 25);
		if (exceedsPageSizeCeiling(pageSize)) {
			throw error(400, 'Page size exceeds the maximum the API serves');
		}

		const page = Number(url.searchParams.get('page') ?? 1);
		const matched = matchFleetMachines(mockFleetMachines, fleetFilterFrom(url));

		return ok(pageOfFleet(matched, page, pageSize));
	}
	if (path === 'machines/ids') {
		// The fixture fleet fits any cap, so truncated is always false here. A picker must not rely
		// on mock mode to prove it handles a capped selection.
		const ids = matchFleetMachines(mockFleetMachines, fleetFilterFrom(url)).map((m) => m.id);

		return ok({ ids, totalCount: ids.length, truncated: false });
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

// Only these request headers are forwarded upstream. Everything else — including client-supplied
// x-forwarded-*, authorization, host, and content-length — is dropped so a browser can neither spoof
// the client address the backend derives from its trusted proxy nor inject an Authorization header.
// x-csrf-token must be forwarded: the backend's JSON antiforgery gate rejects cookie-authenticated
// mutations that arrive without it, so dropping it would 400 every browser-side POST/PUT/DELETE.
const FORWARDABLE_HEADERS = new Set(['content-type', 'accept', 'cookie', 'x-csrf-token']);

// Production / non-mock dev: proxy to the real backend, forwarding method, an allowlisted set of
// headers (cookies included), and the body. The response is mirrored verbatim.
async function proxy(event: Parameters<RequestHandler>[0]): Promise<Response> {
	const path = event.params.path ?? '';
	if (path.split('/').includes('..')) {
		throw error(400, 'Invalid path');
	}

	const search = event.url.search;
	const upstreamUrl = `${API_BASE}/api/v1/${path}${search}`;

	const upstreamHeaders = new Headers();
	for (const [k, v] of event.request.headers) {
		if (FORWARDABLE_HEADERS.has(k.toLowerCase())) {
			upstreamHeaders.set(k, v);
		}
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
