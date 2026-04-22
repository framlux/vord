// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { UserDto, SubscriptionDto } from '$lib/api/types';

const { mockPage } = vi.hoisted(() => {
    const mockPage = {
        url: new URL('http://localhost/dashboard'),
        params: {},
        route: { id: '/dashboard' },
        status: 200,
        error: null,
        data: {},
        form: null
    };

    return { mockPage };
});

vi.mock('$app/state', () => ({
    page: mockPage
}));

vi.mock('$lib/api/client', () => ({
    ApiClient: class {
        switchTenant = vi.fn().mockResolvedValue(undefined);
    }
}));

vi.mock('$app/environment', () => ({
    browser: true
}));

vi.mock('$lib/stores/theme.svelte', () => ({
    getTheme: vi.fn(() => 'light'),
    setTheme: vi.fn()
}));

import AppShell from './AppShell.svelte';

function makeUser(overrides: Partial<UserDto> = {}): UserDto {
    return {
        id: 1,
        name: 'Jonathan Miller',
        email: 'jonathan@acme.co',
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

function setRoute(pathname: string) {
    mockPage.url = new URL(`http://localhost${pathname}`);
}

describe('AppShell', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        setRoute('/dashboard');
    });

    describe('tab visibility by role', () => {
        it('should not show tab bar for viewer (only Fleet items visible)', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '3' }]
            });
            render(AppShell, { props: { user } });

            const fleetLinks = screen.queryAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Fleet'
            );
            expect(fleetLinks).toHaveLength(0);
            expect(screen.getByText('Dashboard')).toBeInTheDocument();
        });

        it('should show Fleet and Settings tabs for TenantAdmin', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            const fleetLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Fleet'
            );
            expect(fleetLinks.length).toBeGreaterThan(0);

            const settingsLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Settings'
            );
            expect(settingsLinks.length).toBeGreaterThan(0);

            const adminLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Admin'
            );
            expect(adminLinks).toHaveLength(0);
        });

        it('should show Fleet, Settings, and Admin tabs for GlobalAdmin', () => {
            const user = makeUser({ isGlobalAdmin: true });
            render(AppShell, { props: { user } });

            const adminLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Admin'
            );
            expect(adminLinks.length).toBeGreaterThan(0);
        });

        it('should completely hide Admin tab for non-global-admin users', () => {
            const user = makeUser({
                isGlobalAdmin: false,
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            const adminLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Admin'
            );
            expect(adminLinks).toHaveLength(0);
        });
    });

    describe('Fleet tab items', () => {
        it('should show core Fleet items for all users', () => {
            setRoute('/dashboard');
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '3' }]
            });
            render(AppShell, { props: { user } });

            expect(screen.getByText('Dashboard')).toBeInTheDocument();
            expect(screen.getByText('Machines')).toBeInTheDocument();
            expect(screen.getByText('Search')).toBeInTheDocument();
            expect(screen.getByText('SSH Sessions')).toBeInTheDocument();
        });

        it('should show Register link for MachineAdmin users', () => {
            setRoute('/dashboard');
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '2' }]
            });
            render(AppShell, { props: { user } });

            expect(screen.getByText('Register')).toBeInTheDocument();
        });

        it('should not show Register link for Viewer users', () => {
            setRoute('/dashboard');
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '3' }]
            });
            render(AppShell, { props: { user } });

            expect(screen.queryByText('Register')).not.toBeInTheDocument();
        });
    });

    describe('Settings tab items', () => {
        it('should show Settings items when Settings tab is active', async () => {
            setRoute('/settings');
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            expect(screen.getByText('General')).toBeInTheDocument();
            expect(screen.getByText('Members')).toBeInTheDocument();
            expect(screen.getByText('Signing Keys')).toBeInTheDocument();
            expect(screen.getByText('Billing')).toBeInTheDocument();
            expect(screen.getByText('Audit Log')).toBeInTheDocument();
        });
    });

    describe('active link highlighting', () => {
        it('should apply active class to current route item', () => {
            setRoute('/dashboard');
            const user = makeUser();
            render(AppShell, { props: { user } });

            const dashboardLink = screen.getByText('Dashboard').closest('a');
            expect(dashboardLink?.className).toContain('bg-primary-500');
        });

        it('should not apply active class to non-current route items', () => {
            setRoute('/dashboard');
            const user = makeUser();
            render(AppShell, { props: { user } });

            const machinesLink = screen.getByText('Machines').closest('a');
            expect(machinesLink?.className).not.toContain('bg-primary-500');
        });
    });

    describe('tab links', () => {
        it('should have correct href on Fleet tab', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            const fleetLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Fleet'
            );
            expect(fleetLinks[0]).toHaveAttribute('href', '/dashboard');
        });

        it('should have correct href on Settings tab', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            const settingsLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Settings'
            );
            expect(settingsLinks[0]).toHaveAttribute('href', '/settings');
        });

        it('should have correct href on Admin tab', () => {
            const user = makeUser({ isGlobalAdmin: true });
            render(AppShell, { props: { user } });

            const adminLinks = screen.getAllByRole('link').filter(
                (el) => el.textContent?.trim() === 'Admin'
            );
            expect(adminLinks[0]).toHaveAttribute('href', '/admin');
        });
    });

    describe('org switcher integration', () => {
        it('should render org switcher with active tenant name', () => {
            const user = makeUser({
                tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }]
            });
            render(AppShell, { props: { user } });

            expect(screen.getByText('Acme Corp')).toBeInTheDocument();
        });

        it('should show plan tier when subscription is provided', () => {
            const user = makeUser();
            const subscription = makeSubscription({ tier: 'Pro' });
            render(AppShell, { props: { user, subscription } });

            expect(screen.getByText('Pro Plan')).toBeInTheDocument();
        });
    });

    describe('user footer', () => {
        it('should render user initial in avatar', () => {
            const user = makeUser({ name: 'Jonathan Miller' });
            render(AppShell, { props: { user } });

            expect(screen.getByText('J')).toBeInTheDocument();
        });

        it('should render user name in footer', () => {
            const user = makeUser({ name: 'Jonathan Miller' });
            render(AppShell, { props: { user } });

            expect(screen.getByText('Jonathan Miller')).toBeInTheDocument();
        });

        it('should fall back to email initial when name is empty', () => {
            const user = makeUser({ name: '', email: 'test@example.com' });
            render(AppShell, { props: { user } });

            expect(screen.getByText('T')).toBeInTheDocument();
        });

        it('should open user menu popover when avatar is clicked', async () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            const userMenuButton = screen.getByLabelText('User menu');
            await fireEvent.click(userMenuButton);

            expect(screen.getByText('Account Settings')).toBeInTheDocument();
            expect(screen.getByText('Notifications')).toBeInTheDocument();
            expect(screen.getByText('Log out')).toBeInTheDocument();
        });

        it('should have correct href on account links', async () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            await fireEvent.click(screen.getByLabelText('User menu'));

            const settingsLink = screen.getByText('Account Settings').closest('a');
            expect(settingsLink).toHaveAttribute('href', '/account/settings');

            const notificationsLink = screen.getByText('Notifications').closest('a');
            expect(notificationsLink).toHaveAttribute('href', '/account/notifications');

            const logoutLink = screen.getByText('Log out').closest('a');
            expect(logoutLink).toHaveAttribute('href', '/auth/logout');
        });
    });

    describe('mobile nav', () => {
        it('should show mobile hamburger button', () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            expect(screen.getByLabelText('Open navigation menu')).toBeInTheDocument();
        });

        it('should show VordFleet brand in mobile header', () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            const brandElements = screen.getAllByText('VordFleet');
            expect(brandElements.length).toBeGreaterThan(0);
        });

        it('should open mobile overlay when hamburger is clicked', async () => {
            const user = makeUser();
            const { container } = render(AppShell, { props: { user } });

            await fireEvent.click(screen.getByLabelText('Open navigation menu'));

            const overlay = container.querySelector('.animate-fade-overlay');
            expect(overlay).not.toBeNull();
        });
    });

    describe('user menu popover links', () => {
        it('should close user menu when Account Settings is clicked', async () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            await fireEvent.click(screen.getByLabelText('User menu'));
            expect(screen.getByText('Account Settings')).toBeInTheDocument();

            await fireEvent.click(screen.getByText('Account Settings'));
            expect(screen.queryByText('Account Settings')).not.toBeInTheDocument();
        });

        it('should close user menu when Log out is clicked', async () => {
            const user = makeUser();
            render(AppShell, { props: { user } });

            await fireEvent.click(screen.getByLabelText('User menu'));
            expect(screen.getByText('Log out')).toBeInTheDocument();

            await fireEvent.click(screen.getByText('Log out'));
            expect(screen.queryByText('Log out')).not.toBeInTheDocument();
        });
    });
});
