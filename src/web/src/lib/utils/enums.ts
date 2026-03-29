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
