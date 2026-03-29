// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { hasRole, canViewMachines, canAdminMachines, canAdminTenant, isGlobalAdmin } from './roles';
import type { UserDto } from '$lib/api/types';
import { UserAccountRole } from '$lib/api/types';

function makeUser(overrides: Partial<UserDto> = {}): UserDto {
    return {
        id: 1,
        name: 'Test User',
        email: 'test@example.com',
        avatar: '',
        isGlobalAdmin: false,
        uniqueId: 'uid-1',
        needsOnboarding: false,
        tenants: [],
        activeTenantId: null,
        ...overrides
    };
}

describe('UserAccountRole enum alignment', () => {
    it('should have TenantAdmin as 1', () => {
        expect(String(UserAccountRole.TenantAdmin)).toBe('1');
    });

    it('should have MachineAdmin as 2', () => {
        expect(String(UserAccountRole.MachineAdmin)).toBe('2');
    });

    it('should have Viewer as 3', () => {
        expect(String(UserAccountRole.Viewer)).toBe('3');
    });
});

describe('hasRole', () => {
    it('should return false for null user', () => {
        expect(hasRole(null, '1')).toBe(false);
    });

    it('should return true for global admin regardless of roles', () => {
        const user = makeUser({ isGlobalAdmin: true, tenants: [] });
        expect(hasRole(user, '1')).toBe(true);
    });

    it('should return true when user has matching tenant role', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '2' }]
        });
        expect(hasRole(user, '2')).toBe(true);
    });

    it('should return false when user does not have matching tenant role', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '3' }]
        });
        expect(hasRole(user, '1')).toBe(false);
    });

    it('should return true when any of multiple roles match', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '3' }]
        });
        expect(hasRole(user, '1', '2', '3')).toBe(true);
    });

    it('should only check roles for the active tenant', () => {
        const user = makeUser({
            activeTenantId: 2,
            tenants: [
                { tenantId: 1, tenantName: 'Org A', role: '3' },
                { tenantId: 2, tenantName: 'Org B', role: '1' }
            ]
        });
        expect(hasRole(user, '1')).toBe(true);
    });

    it('should return false when role exists in non-active tenant only', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [
                { tenantId: 1, tenantName: 'Org A', role: '3' },
                { tenantId: 2, tenantName: 'Org B', role: '1' }
            ]
        });
        expect(hasRole(user, '1')).toBe(false);
    });

    it('should return false when activeTenantId is null for non-admin', () => {
        const user = makeUser({
            activeTenantId: null,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '1' }]
        });
        expect(hasRole(user, '1')).toBe(false);
    });
});

describe('canViewMachines', () => {
    it('should return false for null user', () => {
        expect(canViewMachines(null)).toBe(false);
    });

    it('should return true for Viewer role (3)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '3' }]
        });
        expect(canViewMachines(user)).toBe(true);
    });

    it('should return true for MachineAdmin role (2)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '2' }]
        });
        expect(canViewMachines(user)).toBe(true);
    });

    it('should return true for TenantAdmin role (1)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '1' }]
        });
        expect(canViewMachines(user)).toBe(true);
    });

    it('should return false for user with no tenants', () => {
        const user = makeUser({ tenants: [] });
        expect(canViewMachines(user)).toBe(false);
    });
});

describe('canAdminMachines', () => {
    it('should return false for null user', () => {
        expect(canAdminMachines(null)).toBe(false);
    });

    it('should return true for TenantAdmin (1)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '1' }]
        });
        expect(canAdminMachines(user)).toBe(true);
    });

    it('should return true for MachineAdmin (2)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '2' }]
        });
        expect(canAdminMachines(user)).toBe(true);
    });

    it('should return false for Viewer (3)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '3' }]
        });
        expect(canAdminMachines(user)).toBe(false);
    });
});

describe('canAdminTenant', () => {
    it('should return false for null user', () => {
        expect(canAdminTenant(null)).toBe(false);
    });

    it('should return true for TenantAdmin (1)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '1' }]
        });
        expect(canAdminTenant(user)).toBe(true);
    });

    it('should return false for MachineAdmin (2)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '2' }]
        });
        expect(canAdminTenant(user)).toBe(false);
    });

    it('should return false for Viewer (3)', () => {
        const user = makeUser({
            activeTenantId: 1,
            tenants: [{ tenantId: 1, tenantName: 'Org', role: '3' }]
        });
        expect(canAdminTenant(user)).toBe(false);
    });
});

describe('isGlobalAdmin', () => {
    it('should return false for null user', () => {
        expect(isGlobalAdmin(null)).toBe(false);
    });

    it('should return true when user is global admin', () => {
        const user = makeUser({ isGlobalAdmin: true });
        expect(isGlobalAdmin(user)).toBe(true);
    });

    it('should return false when user is not global admin', () => {
        const user = makeUser({ isGlobalAdmin: false });
        expect(isGlobalAdmin(user)).toBe(false);
    });
});
