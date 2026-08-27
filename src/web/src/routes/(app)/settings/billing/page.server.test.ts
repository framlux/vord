// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';

// vi.mock factories are hoisted above the module body, so anything they close over has to be
// hoisted with them or it is still in the temporal dead zone when the factory runs.
const { publicEnv, apiMock } = vi.hoisted(() => ({
	publicEnv: {} as Record<string, string | undefined>,
	apiMock: {
		getUpcomingInvoice: vi.fn(),
		getInvoices: vi.fn(),
		getUsageHistory: vi.fn(),
		getBillingCatalog: vi.fn()
	}
}));

vi.mock('$env/dynamic/public', () => ({ env: publicEnv }));
vi.mock('$lib/api/server', () => ({
	createServerApiClient: () => apiMock,
	csrfFor: () => undefined
}));

import { load } from './+page.server';
import { UserAccountRole } from '$lib/api/types';

type LoadEvent = Parameters<typeof load>[0];

function makeEvent(user: unknown): LoadEvent {
	return {
		fetch: vi.fn(),
		cookies: { get: () => undefined },
		locals: { user }
	} as unknown as LoadEvent;
}

/**
 * A tenant admin, which is the role the route requires before it considers anything else.
 * hasRole matches on the active tenant's entry in `tenants`, comparing against the stringified
 * UserAccountRole value rather than its name.
 */
function tenantAdmin(selfHosted: boolean | undefined) {
	return {
		isGlobalAdmin: false,
		activeTenantId: 1,
		tenants: [{ tenantId: 1, role: String(UserAccountRole.TenantAdmin) }],
		deployment: selfHosted === undefined ? undefined : { selfHosted }
	};
}

/** Takes the awaited value rather than a Promise, because `load` is typed MaybePromise. */
async function statusOf(result: unknown): Promise<number> {
	try {
		await result;
	} catch (thrown) {
		return (thrown as { status: number }).status;
	}

	throw new Error('expected the load to throw, but it resolved');
}

describe('billing +page.server load — deployment mode', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		publicEnv.PUBLIC_BILLING_URL = 'https://billing.example.com';
		apiMock.getUpcomingInvoice.mockResolvedValue(null);
		apiMock.getInvoices.mockResolvedValue([]);
		apiMock.getUsageHistory.mockResolvedValue([]);
		apiMock.getBillingCatalog.mockResolvedValue([]);
	});

	it('404s in self-hosted mode, because there is no subscription to manage', async () => {
		expect(await statusOf(load(makeEvent(tenantAdmin(true))))).toBe(404);

		// The 404 must come before any billing call. Reaching the API at all would mean a
		// self-hosted deployment talking to a billing service it does not have.
		expect(apiMock.getBillingCatalog).not.toHaveBeenCalled();
	});

	it('loads billing data in hosted mode', async () => {
		const data = await load(makeEvent(tenantAdmin(false)));

		expect(data).toMatchObject({ billingServiceConfigured: true });
		expect(apiMock.getBillingCatalog).toHaveBeenCalledOnce();
	});

	it('treats an absent deployment field as hosted', async () => {
		// An older api-server omits the field. Only the hosted cluster can be mid-rollout, so
		// defaulting to self-hosted would 404 the billing page for real paying tenants.
		const data = await load(makeEvent(tenantAdmin(undefined)));

		expect(data).toMatchObject({ billingServiceConfigured: true });
	});

	it('reports the billing service as unconfigured when no URL is set', async () => {
		// Distinct from the mode question: the deployment bills, but checkout cannot be reached.
		publicEnv.PUBLIC_BILLING_URL = undefined;

		const data = await load(makeEvent(tenantAdmin(false)));

		expect(data).toMatchObject({ billingServiceConfigured: false });
	});

	it('403s a non-admin before consulting the mode at all', async () => {
		const viewer = {
			isGlobalAdmin: false,
			activeTenantId: 1,
			tenants: [{ tenantId: 1, role: String(UserAccountRole.Viewer) }],
			deployment: { selfHosted: false }
		};

		expect(await statusOf(load(makeEvent(viewer)))).toBe(403);
	});
});
