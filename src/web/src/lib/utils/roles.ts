// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { UserDto } from '$lib/api/types';
import { UserAccountRole } from '$lib/api/types';

const TENANT_ADMIN = String(UserAccountRole.TenantAdmin);
const MACHINE_ADMIN = String(UserAccountRole.MachineAdmin);
const VIEWER = String(UserAccountRole.Viewer);

export function hasRole(user: UserDto | null, ...roles: string[]): boolean {
	if (user === null) return false;
	if (user.isGlobalAdmin) return true;
	return user.tenants.some(
		(t) => t.tenantId === user.activeTenantId && roles.includes(t.role)
	);
}

export function canViewMachines(user: UserDto | null): boolean {
	return hasRole(user, TENANT_ADMIN, MACHINE_ADMIN, VIEWER);
}

export function canAdminMachines(user: UserDto | null): boolean {
	return hasRole(user, TENANT_ADMIN, MACHINE_ADMIN);
}

export function canAdminTenant(user: UserDto | null): boolean {
	return hasRole(user, TENANT_ADMIN);
}

export function isGlobalAdmin(user: UserDto | null): boolean {
	return user?.isGlobalAdmin ?? false;
}
