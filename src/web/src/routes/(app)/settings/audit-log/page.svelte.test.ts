// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { AuditLogEntryDto, PaginatedResponse } from '$lib/api/types';
import AuditLogPage from './+page.svelte';

vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/state', () => ({ page: { url: new URL('http://localhost/settings/audit-log') } }));

function makeEntry(overrides: Partial<AuditLogEntryDto> = {}): AuditLogEntryDto {
	return {
		id: 1,
		userEmail: null,
		userId: null,
		machineId: null,
		action: 'SubscriptionUpgraded',
		resourceType: 'Subscription',
		resourceId: '7',
		details: null,
		ipAddress: null,
		timestamp: '2026-10-05T12:00:00Z',
		...overrides
	};
}

function makeData(items: AuditLogEntryDto[]) {
	const auditLog: PaginatedResponse<AuditLogEntryDto> = {
		items,
		page: 1,
		pageSize: 25,
		totalCount: items.length,
		totalPages: 1,
		hasPreviousPage: false,
		hasNextPage: false
	};

	return { auditLog, filters: { action: undefined, from: undefined, to: undefined } };
}

describe('audit log action labels', () => {
	it('shows a readable label for an applied enterprise agreement, not the raw action name', () => {
		render(AuditLogPage, {
			props: { data: makeData([makeEntry({ action: 'EnterpriseAgreementApplied' })]) }
		});

		const table = screen.getByRole('table');
		expect(within(table).getByText('Enterprise Agreement Applied')).toBeInTheDocument();
		expect(within(table).queryByText('EnterpriseAgreementApplied')).not.toBeInTheDocument();
	});

	it('offers the enterprise agreement as an action to filter by', () => {
		render(AuditLogPage, { props: { data: makeData([]) } });

		expect(
			screen.getByRole('option', { name: 'Enterprise Agreement Applied' })
		).toBeInTheDocument();
	});

	it('still falls back to the raw action name for an action it has no label for', () => {
		render(AuditLogPage, {
			props: { data: makeData([makeEntry({ action: 'SomeFutureAction' })]) }
		});

		expect(screen.getByText('SomeFutureAction')).toBeInTheDocument();
	});
});
