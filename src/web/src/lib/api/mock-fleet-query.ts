// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { MachineHealthStatus, type FleetMachineDto, type PaginatedResponse } from './types';
import { MAX_PAGE_SIZE } from '../utils/constants';

/**
 * The fleet query mock mode answers with, in one place.
 *
 * Mock mode serves the fleet two ways — the browser-side route and the server-side MockApiClient —
 * and each used to carry its own copy of the search filter, while their ids answers ignored every
 * filter and one of them read a different fixture fleet altogether. In production the list and
 * "select all matching" share one filter implementation so they cannot disagree; mock mode now
 * does the same, which is the only way it can be used to exercise the picker honestly.
 *
 * Health is the one filter the fixtures can answer — FleetMachineDto carries no OS or type — so os
 * and type are accepted by the callers and ignored, and mock mode cannot prove those two work.
 */

export type MockFleetFilter = {
	search?: string;
	healthStatus?: string;
};

export function matchFleetMachines(
	machines: ReadonlyArray<FleetMachineDto>,
	filter: MockFleetFilter
): FleetMachineDto[] {
	const search = filter.search?.trim().toLowerCase() ?? '';
	const wanted = (filter.healthStatus ?? '')
		.split(',')
		.map((s) => s.trim().toLowerCase())
		.filter((s) => s.length > 0);

	return machines.filter((machine) => {
		const matchesSearch =
			search.length === 0 ||
			machine.name.toLowerCase().includes(search) ||
			(machine.hostname ?? '').toLowerCase().includes(search) ||
			(machine.hardwareModel ?? '').toLowerCase().includes(search);

		const matchesHealth =
			wanted.length === 0 ||
			wanted.includes(MachineHealthStatus[machine.healthStatus].toLowerCase());

		return matchesSearch && matchesHealth;
	});
}

/**
 * Whether the API would refuse this page size. Only the ceiling is modelled: that is the rule the
 * server is known to enforce, and a mock that invented a stricter one would fail callers the real
 * API accepts.
 */
export function exceedsPageSizeCeiling(pageSize: number): boolean {
	return pageSize > MAX_PAGE_SIZE;
}

export function pageOfFleet(
	matched: FleetMachineDto[],
	page: number,
	pageSize: number
): PaginatedResponse<FleetMachineDto> {
	const start = (page - 1) * pageSize;
	const totalPages = Math.max(1, Math.ceil(matched.length / pageSize));

	return {
		items: matched.slice(start, start + pageSize),
		page,
		pageSize,
		totalCount: matched.length,
		totalPages,
		hasNextPage: page < totalPages
	} as PaginatedResponse<FleetMachineDto>;
}
