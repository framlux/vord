// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type {
	UserDto,
	SubscriptionDto,
	DashboardSummaryDto,
	PaginatedFleetOverviewDto,
	PaginatedResponse,
	MachineDto,
	MachineDetailDto,
	MachineAuthorizedKeyDto,
	AlertRuleDto,
	FleetSshSessionDto,
	UpdateMachineRequest
} from './types';
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
