// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { OperatingSystem, MachineType } from '$lib/api/types';

/**
 * Returns a human-readable name for the given OperatingSystem enum value.
 */
export function getOsName(os: OperatingSystem): string {
	return OperatingSystem[os] ?? 'Unknown';
}

/**
 * Returns a human-readable name for the given MachineType enum value.
 */
export function getTypeName(type: MachineType): string {
	switch (type) {
		case MachineType.Desktop:
			return 'Desktop';
		case MachineType.Laptop:
			return 'Laptop';
		case MachineType.BareMetalServer:
			return 'Bare Metal Server';
		case MachineType.VirtualMachine:
			return 'Virtual Machine';
		default:
			return 'Unknown';
	}
}

/**
 * Returns filter option arrays derived from enums for use in dropdowns.
 */
export function getOsFilterOptions(): { value: string; label: string }[] {
	return [
		{ value: '', label: 'All OS' },
		...Object.keys(OperatingSystem)
			.filter((key) => isNaN(Number(key)))
			.map((key) => ({ value: key, label: getOsName(OperatingSystem[key as keyof typeof OperatingSystem]) }))
	];
}

export function getTypeFilterOptions(): { value: string; label: string }[] {
	return [
		{ value: '', label: 'All Types' },
		...Object.keys(MachineType)
			.filter((key) => isNaN(Number(key)))
			.map((key) => ({ value: key, label: getTypeName(MachineType[key as keyof typeof MachineType]) }))
	];
}
