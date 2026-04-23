<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
    import {
        LayoutDashboard,
        Monitor,
        Terminal,
        Search,
        Settings,
        Shield,
        Menu,
        Users,
        Key,
        CreditCard,
        ScrollText,
        Bell,
        CircleHelp,
        LogOut
    } from 'lucide-svelte';
    import ThemeToggle from './ThemeToggle.svelte';
    import OrgSwitcher from './OrgSwitcher.svelte';
    import { page } from '$app/state';
    import { canAdminMachines, canAdminTenant, isGlobalAdmin } from '$lib/utils/roles';
    import type { UserDto, SubscriptionDto } from '$lib/api/types';

    import type { Snippet } from 'svelte';

    let { user, subscription = null, children }: {
        user: UserDto;
        subscription?: SubscriptionDto | null;
        children?: Snippet;
    } = $props();

    let mobileMenuOpen = $state(false);
    let userMenuOpen = $state(false);

    type NavTab = 'fleet' | 'settings' | 'admin';

    function deriveTab(pathname: string): NavTab {
        if (pathname.startsWith('/admin')) {
            return 'admin';
        }
        if (pathname.startsWith('/settings')) {
            return 'settings';
        }

        return 'fleet';
    }

    let activeTab = $derived(deriveTab(page.url.pathname));

    let showSettingsTab = $derived(canAdminTenant(user));
    let showAdminTab = $derived(isGlobalAdmin(user));
    let showTabBar = $derived(showSettingsTab || showAdminTab);

    function isActive(path: string): boolean {
        if (path === '/machines' || path === '/settings') {
            return page.url.pathname === path;
        }

        return page.url.pathname.startsWith(path);
    }

    const navItemClass = (path: string) =>
        `flex items-center gap-2.5 rounded-md px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
            isActive(path)
                ? 'bg-primary-500/10 text-primary-600 dark:text-primary-400'
                : 'text-surface-600 hover:bg-surface-100 hover:text-surface-900 dark:text-surface-400 dark:hover:bg-surface-800 dark:hover:text-surface-200'
        }`;

    function getUserInitial(): string {
        if (user.name && user.name.length > 0) {
            return user.name[0].toUpperCase();
        }
        if (user.email && user.email.length > 0) {
            return user.email[0].toUpperCase();
        }

        return '?';
    }

    function handleUserMenuClickOutside(event: MouseEvent) {
        const target = event.target as HTMLElement;
        if (!target.closest('.user-menu-footer')) {
            userMenuOpen = false;
        }
    }
</script>

<svelte:window onclick={handleUserMenuClickOutside} />

{#snippet sidebarContent(onNavigate: (() => void) | undefined)}
    <!-- Logo -->
    <div class="flex h-14 items-center gap-2.5 border-b border-surface-200 px-4 dark:border-surface-700">
        <div class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary-500/10">
            <svg class="h-4 w-4 text-primary-500" viewBox="0 0 24 24" fill="none">
                <path d="M4.5 6 L12 19 L19.5 6" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
                <circle cx="4.5" cy="6" r="2.5" fill="currentColor"/>
                <circle cx="19.5" cy="6" r="2.5" fill="currentColor"/>
                <circle cx="12" cy="19" r="2.5" fill="currentColor"/>
            </svg>
        </div>
        <span class="text-base font-bold text-surface-900 dark:text-surface-50">VordFleet</span>
    </div>

    <!-- Org Switcher -->
    <OrgSwitcher {user} {subscription} />

    <!-- Tab bar -->
    {#if showTabBar}
        <div class="flex gap-1 border-b border-surface-200 px-3 pt-2 dark:border-surface-700">
            <a
                href="/dashboard"
                class="flex-1 border-b-2 pb-1.5 pt-1 text-center text-[11px] font-semibold no-underline transition-colors {activeTab === 'fleet' ? 'border-primary-500 text-primary-500' : 'border-transparent text-surface-400 hover:text-surface-600 dark:hover:text-surface-300'}"
                onclick={onNavigate}
            >
                Fleet
            </a>
            {#if showSettingsTab}
                <a
                    href="/settings"
                    class="flex-1 border-b-2 pb-1.5 pt-1 text-center text-[11px] font-semibold no-underline transition-colors {activeTab === 'settings' ? 'border-primary-500 text-primary-500' : 'border-transparent text-surface-400 hover:text-surface-600 dark:hover:text-surface-300'}"
                    onclick={onNavigate}
                >
                    Settings
                </a>
            {/if}
            {#if showAdminTab}
                <a
                    href="/admin"
                    class="flex-1 border-b-2 pb-1.5 pt-1 text-center text-[11px] font-semibold no-underline transition-colors {activeTab === 'admin' ? 'border-primary-500 text-primary-500' : 'border-transparent text-surface-400 hover:text-surface-600 dark:hover:text-surface-300'}"
                    onclick={onNavigate}
                >
                    Admin
                </a>
            {/if}
        </div>
    {/if}

    <!-- Tab content -->
    <nav class="flex-1 overflow-y-auto px-3 py-3">
        {#if activeTab === 'fleet'}
            <a href="/dashboard" class={navItemClass('/dashboard')} onclick={onNavigate}>
                <LayoutDashboard size={16} />
                Dashboard
            </a>
            <a href="/machines" class={navItemClass('/machines')} onclick={onNavigate}>
                <Monitor size={16} />
                Machines
            </a>
            {#if canAdminMachines(user)}
                <a href="/machines/register" class={navItemClass('/machines/register')} onclick={onNavigate}>
                    <Key size={16} />
                    Register
                </a>
            {/if}
            <a href="/machines/search" class={navItemClass('/machines/search')} onclick={onNavigate}>
                <Search size={16} />
                Search
            </a>
            <a href="/machines/ssh-sessions" class={navItemClass('/machines/ssh-sessions')} onclick={onNavigate}>
                <Terminal size={16} />
                SSH Sessions
            </a>
        {:else if activeTab === 'settings'}
            <a href="/settings" class={navItemClass('/settings')} onclick={onNavigate}>
                <Settings size={16} />
                General
            </a>
            <a href="/settings/members" class={navItemClass('/settings/members')} onclick={onNavigate}>
                <Users size={16} />
                Members
            </a>
            <a href="/settings/signing-keys" class={navItemClass('/settings/signing-keys')} onclick={onNavigate}>
                <Shield size={16} />
                Signing Keys
            </a>
            <hr class="my-2 border-surface-200 dark:border-surface-700" />
            <a href="/settings/billing" class={navItemClass('/settings/billing')} onclick={onNavigate}>
                <CreditCard size={16} />
                Billing
            </a>
            <hr class="my-2 border-surface-200 dark:border-surface-700" />
            <a href="/settings/audit-log" class={navItemClass('/settings/audit-log')} onclick={onNavigate}>
                <ScrollText size={16} />
                Audit Log
            </a>
            <a href="/settings/alerts" class={navItemClass('/settings/alerts')} onclick={onNavigate}>
                <Bell size={16} />
                Alerts
            </a>
        {:else if activeTab === 'admin'}
            <a href="/admin" class={navItemClass('/admin')} onclick={onNavigate}>
                <Shield size={16} />
                System Overview
            </a>
        {/if}
    </nav>

    <!-- User footer -->
    <div class="user-menu-footer relative border-t border-surface-200 px-3 py-2.5 dark:border-surface-700">
        <div class="flex items-center gap-2">
            <button
                onclick={() => { userMenuOpen = !userMenuOpen; }}
                class="flex min-w-0 flex-1 items-center gap-2 rounded-md p-1 transition-colors hover:bg-surface-100 dark:hover:bg-surface-800"
                aria-label="User menu"
                aria-expanded={userMenuOpen}
            >
                <div class="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-full bg-primary-500 text-xs font-semibold text-white">
                    {getUserInitial()}
                </div>
                <div class="min-w-0 flex-1 text-left">
                    <div class="truncate text-[12px] font-medium text-surface-800 dark:text-surface-200">
                        {user.name || user.email}
                    </div>
                </div>
            </button>
            <ThemeToggle />
        </div>

        {#if userMenuOpen}
            <div class="absolute bottom-full left-2 right-2 z-50 mb-1 rounded-lg border border-surface-200 bg-surface-50 shadow-lg dark:border-surface-700 dark:bg-surface-800">
                <div class="border-b border-surface-200 px-3 py-2.5 dark:border-surface-700">
                    <p class="text-[13px] font-medium text-surface-900 dark:text-surface-50">
                        {user.name || 'User'}
                    </p>
                    <p class="truncate text-[11px] text-surface-500">{user.email}</p>
                </div>
                <div class="py-1">
                    <a
                        href="/account/settings"
                        onclick={() => { userMenuOpen = false; if (onNavigate) onNavigate(); }}
                        class="flex items-center gap-2.5 px-3 py-1.5 text-[13px] text-surface-700 transition-colors hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
                    >
                        <Settings size={14} />
                        Account Settings
                    </a>
                    <a
                        href="/account/notifications"
                        onclick={() => { userMenuOpen = false; if (onNavigate) onNavigate(); }}
                        class="flex items-center gap-2.5 px-3 py-1.5 text-[13px] text-surface-700 transition-colors hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
                    >
                        <Bell size={14} />
                        Notifications
                    </a>
                    <a
                        href="https://vordfleet.dev/support"
                        target="_blank"
                        rel="noopener noreferrer"
                        onclick={() => { userMenuOpen = false; }}
                        class="flex items-center gap-2.5 px-3 py-1.5 text-[13px] text-surface-700 transition-colors hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
                    >
                        <CircleHelp size={14} />
                        Support
                    </a>
                </div>
                <div class="border-t border-surface-200 py-1 dark:border-surface-700">
                    <a
                        href="/auth/logout"
                        onclick={() => { userMenuOpen = false; if (onNavigate) onNavigate(); }}
                        class="flex items-center gap-2.5 px-3 py-1.5 text-[13px] text-surface-700 transition-colors hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
                    >
                        <LogOut size={14} />
                        Log out
                    </a>
                </div>
            </div>
        {/if}
    </div>
{/snippet}

<div class="flex h-screen bg-surface-50 dark:bg-surface-900">
    <!-- Sidebar (desktop) -->
    <aside
        class="hidden w-56 flex-shrink-0 flex-col border-r border-surface-200 bg-surface-50/80 backdrop-blur-xl dark:border-surface-700 dark:bg-surface-800/80 lg:flex"
    >
        {@render sidebarContent(undefined)}
    </aside>

    <!-- Mobile overlay -->
    {#if mobileMenuOpen}
        <!-- svelte-ignore a11y_no_static_element_interactions -->
        <div
            class="animate-fade-overlay fixed inset-0 z-40 bg-black/50 lg:hidden"
            role="presentation"
            onclick={(e) => { if (e.target === e.currentTarget) mobileMenuOpen = false; }}
        >
            <aside
                class="animate-slide-in-left absolute left-0 top-0 flex h-full w-56 flex-col bg-surface-50 dark:bg-surface-800"
            >
                {@render sidebarContent(() => { mobileMenuOpen = false; })}
            </aside>
        </div>
    {/if}

    <!-- Main content -->
    <div class="flex flex-1 flex-col overflow-hidden">
        <!-- Mobile top bar -->
        <header class="flex h-12 items-center gap-3 border-b border-surface-200 px-4 dark:border-surface-700 lg:hidden">
            <button
                onclick={() => (mobileMenuOpen = !mobileMenuOpen)}
                class="rounded-md p-1.5 text-surface-500 hover:bg-surface-100 dark:hover:bg-surface-700"
                aria-label="Open navigation menu"
            >
                <Menu size={20} />
            </button>
            <span class="text-sm font-semibold text-surface-900 dark:text-surface-50">VordFleet</span>
        </header>

        <!-- Page content -->
        <main class="animate-fade-in flex-1 overflow-y-auto p-6">
            {@render children?.()}
        </main>
    </div>
</div>
