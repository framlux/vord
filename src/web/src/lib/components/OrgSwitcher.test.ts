// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import OrgSwitcher from './OrgSwitcher.svelte';
import type { UserDto, SubscriptionDto } from '$lib/api/types';

vi.mock('$lib/api/client', () => ({
    ApiClient: class {
        switchTenant = vi.fn().mockResolvedValue(undefined);
    }
}));

function makeUser(overrides: Partial<UserDto> = {}): UserDto {
    return {
        id: 1,
        name: 'Test User',
        email: 'test@example.com',
        avatar: '',
        isGlobalAdmin: false,
        uniqueId: 'uid-1',
        needsOnboarding: false,
        tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }],
        activeTenantId: 1,
        ...overrides
    };
}

function makeSubscription(overrides: Partial<SubscriptionDto> = {}): SubscriptionDto {
    return {
        tier: 'Pro',
        status: 'Active',
        machineLimit: 100,
        machineCount: 42,
        retentionDays: 90,
        currentPeriodEnd: null,
        cancelAtPeriodEnd: false,
        pendingAction: null,
        ...overrides
    };
}

describe('OrgSwitcher', () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    describe('single tenant', () => {
        it('should render the active tenant name', () => {
            const user = makeUser();
            render(OrgSwitcher, { props: { user } });

            expect(screen.getByText('Acme Corp')).toBeInTheDocument();
        });

        it('should render tenant initials in the avatar', () => {
            const user = makeUser();
            render(OrgSwitcher, { props: { user } });

            expect(screen.getByText('AC')).toBeInTheDocument();
        });

        it('should not render the chevron icon for single tenant', () => {
            const user = makeUser();
            const { container } = render(OrgSwitcher, { props: { user } });

            const svgs = container.querySelectorAll('svg');
            const chevronSvg = Array.from(svgs).find(
                (svg) => svg.classList.contains('lucide-chevrons-up-down')
            );
            expect(chevronSvg).toBeUndefined();
        });

        it('should not open dropdown when clicked with single tenant', async () => {
            const user = makeUser();
            render(OrgSwitcher, { props: { user } });

            await fireEvent.click(screen.getByRole('button'));

            expect(screen.queryByText('Personal Workspace')).not.toBeInTheDocument();
        });

        it('should display plan tier when subscription is provided', () => {
            const user = makeUser();
            const subscription = makeSubscription({ tier: 'Pro' });
            render(OrgSwitcher, { props: { user, subscription } });

            expect(screen.getByText('Pro Plan')).toBeInTheDocument();
        });

        it('should not display plan tier when subscription is null', () => {
            const user = makeUser();
            render(OrgSwitcher, { props: { user, subscription: null } });

            expect(screen.queryByText(/Plan$/)).not.toBeInTheDocument();
        });
    });

    describe('multi-tenant', () => {
        function makeMultiTenantUser(): UserDto {
            return makeUser({
                tenants: [
                    { tenantId: 1, tenantName: 'Acme Corp', role: '1' },
                    { tenantId: 2, tenantName: 'Personal Workspace', role: '3' },
                    { tenantId: 3, tenantName: 'Staging Env', role: '2' }
                ],
                activeTenantId: 1
            });
        }

        it('should render the active tenant name', () => {
            render(OrgSwitcher, { props: { user: makeMultiTenantUser() } });

            expect(screen.getByText('Acme Corp')).toBeInTheDocument();
        });

        it('should show dropdown with all tenants when clicked', async () => {
            render(OrgSwitcher, { props: { user: makeMultiTenantUser() } });

            await fireEvent.click(screen.getByLabelText('Switch organization'));

            expect(screen.getAllByText('Acme Corp')).toHaveLength(2);
            expect(screen.getByText('Personal Workspace')).toBeInTheDocument();
            expect(screen.getByText('Staging Env')).toBeInTheDocument();
        });

        it('should close dropdown when clicking the same tenant', async () => {
            render(OrgSwitcher, { props: { user: makeMultiTenantUser() } });

            await fireEvent.click(screen.getByLabelText('Switch organization'));
            expect(screen.getByText('Personal Workspace')).toBeInTheDocument();

            const dropdownButtons = screen.getAllByRole('button').filter(
                (btn) => btn.closest('.absolute') && btn.textContent?.includes('Acme Corp')
            );
            expect(dropdownButtons.length).toBeGreaterThan(0);

            await fireEvent.click(dropdownButtons[0]);
            expect(screen.queryByText('Personal Workspace')).not.toBeInTheDocument();
        });

        it('should render initials for multi-word tenant names', () => {
            render(OrgSwitcher, { props: { user: makeMultiTenantUser() } });

            expect(screen.getByText('AC')).toBeInTheDocument();
        });

        it('should render single initial for single-word tenant names', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Framlux', role: '1' }],
                activeTenantId: 1
            });
            render(OrgSwitcher, { props: { user } });

            expect(screen.getByText('F')).toBeInTheDocument();
        });
    });

    describe('edge cases', () => {
        it('should handle user with no tenants', () => {
            const user = makeUser({ tenants: [], activeTenantId: null });
            render(OrgSwitcher, { props: { user } });

            expect(screen.getByText('No Organization')).toBeInTheDocument();
            expect(screen.getByText('?')).toBeInTheDocument();
        });

        it('should handle mismatched activeTenantId by falling back to first tenant', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Fallback Org', role: '1' }],
                activeTenantId: 999
            });
            render(OrgSwitcher, { props: { user } });

            expect(screen.getByText('Fallback Org')).toBeInTheDocument();
        });

        it('should capitalize tier label correctly', () => {
            const user = makeUser();
            const subscription = makeSubscription({ tier: 'team' });
            render(OrgSwitcher, { props: { user, subscription } });

            expect(screen.getByText('Team Plan')).toBeInTheDocument();
        });

        it('should show question mark for tenant with empty name', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: '', role: '1' }],
                activeTenantId: 1
            });
            render(OrgSwitcher, { props: { user } });

            const avatars = screen.getAllByText('?');
            expect(avatars.length).toBeGreaterThan(0);
        });

        it('should not display plan text when tier is null', () => {
            const user = makeUser();
            const subscription = makeSubscription({ tier: null as unknown as string });
            render(OrgSwitcher, { props: { user, subscription } });

            expect(screen.queryByText(/Plan$/)).not.toBeInTheDocument();
        });
    });

    describe('tenant switching', () => {
        it('should call switchTenant API when selecting a different tenant', async () => {
            const user = makeUser({
                tenants: [
                    { tenantId: 1, tenantName: 'Acme Corp', role: '1' },
                    { tenantId: 2, tenantName: 'Other Org', role: '3' }
                ],
                activeTenantId: 1
            });

            const reloadSpy = vi.fn();
            Object.defineProperty(window, 'location', {
                value: { reload: reloadSpy },
                writable: true,
                configurable: true
            });

            render(OrgSwitcher, { props: { user } });

            await fireEvent.click(screen.getByLabelText('Switch organization'));
            await fireEvent.click(screen.getByText('Other Org'));
        });

        it('should not disable buttons before switching starts', () => {
            const user = makeUser({
                tenants: [
                    { tenantId: 1, tenantName: 'Acme Corp', role: '1' },
                    { tenantId: 2, tenantName: 'Other Org', role: '3' }
                ],
                activeTenantId: 1
            });

            render(OrgSwitcher, { props: { user } });

            const switchButton = screen.getByLabelText('Switch organization');
            expect(switchButton.hasAttribute('disabled')).toBe(false);
        });
    });
});
