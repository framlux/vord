// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { ApiError } from './client';
import { exceedsPageSizeCeiling, matchFleetMachines, pageOfFleet, type MockFleetFilter } from './mock-fleet-query';
import type {
	UserDto,
	SubscriptionDto,
	DashboardSummaryDto,
	FleetMachineDto,
	MachineSearchParams,
	PaginatedFleetOverviewDto,
	PaginatedResponse,
	MachineDto,
	MachineIdSelectionDto,
	MachineDetailDto,
	MachineAuthorizedKeyDto,
	AlertRuleDto,
	AlertEventDto,
	IntegrationEndpointDto,
	IntegrationProviderDto,
	FleetSshSessionDto,
	UpdateMachineRequest
} from './types';
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
} from './mock-fixtures';

// Drop-in replacement for ApiClient used only when VORD_API_MOCK=true in a dev
// build. Implements the subset of methods the screenshot routes call. Methods
// absent from this class will throw "is not a function" at runtime —
// intentional, so we don't silently fall through on screens we never validated.
// Tree-shaken out of production bundles via `dev` gates at all call sites.

/* @__PURE__ */
export class MockApiClient {
	async getMe(): Promise<UserDto> {
		return mockUser;
	}

	async getSubscription(): Promise<SubscriptionDto> {
		return mockSubscription;
	}

	async getDashboardSummary(): Promise<DashboardSummaryDto> {
		return {
			totalMachines: mockFleetOverview.summary.totalMachines,
			onlineMachines: mockFleetOverview.summary.onlineMachines,
			pendingApprovals: 0
		};
	}

	async getFleetOverview(): Promise<PaginatedFleetOverviewDto> {
		return mockFleetOverview;
	}

	async getMachines(): Promise<PaginatedResponse<MachineDto>> {
		return mockMachineList;
	}

	// Resolved from the same fleet and the same filter as searchMachines, so select-all is exactly
	// what the list shows. It used to read a different fixture fleet and ignore every filter. The
	// fixture fleet is small enough to fit any cap, so mock mode never exercises the truncated
	// branch; a picker must not rely on mock mode to prove it handles one.
	async getMachineIds(params?: MockFleetFilter & { os?: string; type?: string }): Promise<MachineIdSelectionDto> {
		const ids = matchFleetMachines(mockFleetMachines, params ?? {}).map((m) => m.id);

		return { ids, totalCount: ids.length, truncated: false };
	}

	// Filtering and paging are applied here rather than returning the whole fixture fleet, because a
	// picker that pages is only exercised by a source that actually pages. The page-size ceiling is
	// enforced as the API enforces it, so a caller that regressed past it fails here too.
	async searchMachines(params: MachineSearchParams): Promise<PaginatedResponse<FleetMachineDto>> {
		const pageSize = params.pageSize ?? 25;
		if (exceedsPageSizeCeiling(pageSize)) {
			throw new ApiError(400, 'Page size exceeds the maximum the API serves');
		}

		const matched = matchFleetMachines(mockFleetMachines, params);

		return pageOfFleet(matched, params.page ?? 1, pageSize);
	}

	async getMachine(id: number): Promise<MachineDto> {
		const machine = mockMachineById.get(id);
		if (machine === undefined) {
			throw new Error(`MockApiClient: machine ${id} not found in fixtures`);
		}

		return machine;
	}

	async getMachineDetail(id: number): Promise<MachineDetailDto> {
		const detail = mockMachineDetailById.get(id);
		if (detail === undefined) {
			throw new Error(`MockApiClient: machine detail ${id} not found in fixtures`);
		}

		return detail;
	}

	async getMachineAuthorizedKeys(_machineId: number): Promise<MachineAuthorizedKeyDto[]> {
		return mockMachineAuthorizedKeys;
	}

	async getMachineAlertRules(machineId: number): Promise<AlertRuleDto[]> {
		return getMockMachineAlertRules(machineId);
	}

	async getAlertRules(): Promise<AlertRuleDto[]> {
		return mockAlertRules;
	}

	async getFleetSshSessions(): Promise<PaginatedResponse<FleetSshSessionDto>> {
		return mockFleetSshSessions;
	}

	// The alerts settings screen reads three more collections alongside the rules. There are no
	// fixtures for them, and empty is the honest answer: a demo fleet has fired nothing and connected
	// nothing, which is also the state the page's empty branches are least often seen in.
	async getAlertEvents(): Promise<PaginatedResponse<AlertEventDto>> {
		return { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0, hasNextPage: false, hasPreviousPage: false };
	}

	async getIntegrations(): Promise<IntegrationEndpointDto[]> {
		return [];
	}

	async getIntegrationProviders(): Promise<IntegrationProviderDto[]> {
		return [];
	}

	// No-op success stubs — let mid-screenshot clicks (tenant switch, rename, ack)
	// resolve cleanly instead of throwing toasts.

	async switchTenant(_tenantId: number): Promise<void> {
		// Mock tenant switching is a no-op
	}

	async updateMachine(id: number, data: UpdateMachineRequest): Promise<MachineDto> {
		const existing = mockMachineById.get(id);
		if (existing === undefined) {
			throw new Error(`MockApiClient: machine ${id} not found in fixtures`);
		}

		return {
			...existing,
			name: data.name,
			description: data.description ?? existing.description,
			location: data.location ?? existing.location
		};
	}

	async acknowledgeAlertEvent(_id: number): Promise<void> {
		// Mock alert ack is a no-op
	}
}
