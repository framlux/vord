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
        X,
        Users,
        Key,
        CreditCard,
        ScrollText,
        Bell,
        ChevronDown,
        ChevronRight
    } from 'lucide-svelte';
    import ThemeToggle from './ThemeToggle.svelte';
    import TenantSwitcher from './TenantSwitcher.svelte';
    import UserMenu from './UserMenu.svelte';
    import { page } from '$app/state';
    import { canAdminMachines, canAdminTenant, isGlobalAdmin } from '$lib/utils/roles';
    import type { UserDto } from '$lib/api/types';

    import type { Snippet } from 'svelte';

    let { user, children }: { user: UserDto; children: Snippet } = $props();

    let mobileMenuOpen = $state(false);
    let settingsExpanded = $state(page.url.pathname.startsWith('/settings'));

    function isExactMatch(path: string): boolean {
        return page.url.pathname === path;
    }

    function isChildActive(parentPath: string): boolean {
        return page.url.pathname.startsWith(parentPath) && page.url.pathname !== parentPath;
    }

    function isActive(path: string): boolean {
        if (path === '/machines' || path === '/settings') {
            return page.url.pathname === path;
        }

        return page.url.pathname.startsWith(path);
    }

    const navLinkClass = (path: string) =>
        `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-all ${
            isActive(path)
                ? 'border-l-2 border-primary-500 bg-primary-500/10 text-primary-600 dark:text-primary-400'
                : 'border-l-2 border-transparent text-surface-600 hover:bg-surface-100 hover:text-surface-900 dark:text-surface-400 dark:hover:bg-surface-800 dark:hover:text-surface-200'
        }`;

    const parentLinkClass = (parentPath: string) => {
        if (isExactMatch(parentPath)) {
            return 'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-all border-l-2 border-primary-500 bg-primary-500/10 text-primary-600 dark:text-primary-400';
        }
        if (isChildActive(parentPath)) {
            return 'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-all border-l-2 border-primary-500/40 bg-primary-500/5 text-primary-600 dark:text-primary-400';
        }

        return 'flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-all border-l-2 border-transparent text-surface-600 hover:bg-surface-100 hover:text-surface-900 dark:text-surface-400 dark:hover:bg-surface-800 dark:hover:text-surface-200';
    };

    const childLinkClass = (path: string) =>
        `flex items-center gap-3 rounded-lg py-1.5 pl-9 pr-3 text-sm font-medium transition-all ${
            isActive(path)
                ? 'text-primary-600 dark:text-primary-400'
                : 'text-surface-500 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200'
        }`;

    function toggleSettings(e: MouseEvent) {
        e.preventDefault();
        e.stopPropagation();
        settingsExpanded = !settingsExpanded;
    }
</script>

{#snippet navLinks(onNavigate: (() => void) | undefined)}
    <!-- FLEET -->
    <p class="px-4 pb-1 text-[10px] font-semibold uppercase tracking-widest text-surface-400 dark:text-surface-500">Fleet</p>

    <a href="/dashboard" class={navLinkClass('/dashboard')} onclick={onNavigate}>
        <LayoutDashboard size={18} />
        Dashboard
    </a>
    <a href="/machines" class={parentLinkClass('/machines')} onclick={onNavigate}>
        <Monitor size={18} />
        Machines
    </a>
    <div class="relative ml-[21px] border-l border-surface-200 dark:border-surface-700">
        {#if canAdminMachines(user)}
            <a href="/machines/register" class={childLinkClass('/machines/register')} onclick={onNavigate}>
                <Key size={14} />
                Register
            </a>
        {/if}
        <a href="/machines/search" class={childLinkClass('/machines/search')} onclick={onNavigate}>
            <Search size={14} />
            Search
        </a>
        <a href="/machines/ssh-sessions" class={childLinkClass('/machines/ssh-sessions')} onclick={onNavigate}>
            <Terminal size={14} />
            SSH Sessions
        </a>
    </div>

    {#if canAdminTenant(user)}
        <hr class="mx-4 my-3 border-surface-200 dark:border-surface-700" />
        <p class="px-4 pb-1 text-[10px] font-semibold uppercase tracking-widest text-surface-400 dark:text-surface-500">Organization</p>

        <div class="flex items-center">
            <a href="/settings" class="{parentLinkClass('/settings')} flex-1" onclick={onNavigate}>
                <Settings size={18} />
                Settings
            </a>
            <button
                onclick={toggleSettings}
                class="mr-2 rounded p-1 text-surface-400 hover:bg-surface-100 hover:text-surface-600 dark:hover:bg-surface-700 dark:hover:text-surface-300"
                aria-label={settingsExpanded ? 'Collapse settings' : 'Expand settings'}
            >
                {#if settingsExpanded}
                    <ChevronDown size={14} />
                {:else}
                    <ChevronRight size={14} />
                {/if}
            </button>
        </div>

        {#if settingsExpanded}
            <div class="relative ml-[21px] border-l border-surface-200 dark:border-surface-700">
                <a href="/settings/members" class={childLinkClass('/settings/members')} onclick={onNavigate}>
                    <Users size={14} />
                    Members
                </a>
                <a href="/settings/signing-keys" class={childLinkClass('/settings/signing-keys')} onclick={onNavigate}>
                    <Shield size={14} />
                    Signing Keys
                </a>
                <a href="/settings/billing" class={childLinkClass('/settings/billing')} onclick={onNavigate}>
                    <CreditCard size={14} />
                    Billing
                </a>
                <a href="/settings/audit-log" class={childLinkClass('/settings/audit-log')} onclick={onNavigate}>
                    <ScrollText size={14} />
                    Audit Log
                </a>
                <a href="/settings/alerts" class={childLinkClass('/settings/alerts')} onclick={onNavigate}>
                    <Bell size={14} />
                    Alerts
                </a>
            </div>
        {/if}
    {/if}

    {#if isGlobalAdmin(user)}
        <hr class="mx-4 my-3 border-surface-200 dark:border-surface-700" />
        <p class="px-4 pb-1 text-[10px] font-semibold uppercase tracking-widest text-surface-400 dark:text-surface-500">System</p>

        <a href="/admin" class={navLinkClass('/admin')} onclick={onNavigate}>
            <Shield size={18} />
            Admin
        </a>
    {/if}
{/snippet}

<div class="flex h-screen bg-surface-50 dark:bg-surface-900">
    <!-- Sidebar (desktop) -->
    <aside
        class="hidden w-64 flex-shrink-0 border-r border-surface-200 bg-surface-50/80 backdrop-blur-xl dark:border-surface-700 dark:bg-surface-800/80 lg:block"
    >
        <div class="flex h-full flex-col">
            <div class="flex h-16 items-center gap-2.5 border-b border-surface-200 px-6 dark:border-surface-700">
                <div class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary-500/10">
                    <svg class="h-4 w-4 text-primary-500" viewBox="0 0 24 24" fill="none">
                        <path d="M4.5 6 L12 19 L19.5 6" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
                        <circle cx="4.5" cy="6" r="2.5" fill="currentColor"/>
                        <circle cx="19.5" cy="6" r="2.5" fill="currentColor"/>
                        <circle cx="12" cy="19" r="2.5" fill="currentColor"/>
                    </svg>
                </div>
                <span class="text-lg font-bold text-surface-900 dark:text-surface-50">VordFleet</span>
            </div>
            <nav class="flex-1 space-y-1 overflow-y-auto p-4 pt-5">
                {@render navLinks(undefined)}
            </nav>
        </div>
    </aside>

    <!-- Mobile overlay -->
    {#if mobileMenuOpen}
        <div class="fixed inset-0 z-40 bg-black/50 lg:hidden" role="presentation">
            <aside class="absolute left-0 top-0 h-full w-64 bg-surface-50 dark:bg-surface-800">
                <div class="flex h-16 items-center justify-between border-b border-surface-200 px-6 dark:border-surface-700">
                    <div class="flex items-center gap-2.5">
                        <div class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary-500/10">
                            <svg class="h-4 w-4 text-primary-500" viewBox="0 0 24 24" fill="none">
                                <path d="M4.5 6 L12 19 L19.5 6" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
                                <circle cx="4.5" cy="6" r="2.5" fill="currentColor"/>
                                <circle cx="19.5" cy="6" r="2.5" fill="currentColor"/>
                                <circle cx="12" cy="19" r="2.5" fill="currentColor"/>
                            </svg>
                        </div>
                        <span class="text-lg font-bold text-surface-900 dark:text-surface-50">VordFleet</span>
                    </div>
                    <button onclick={() => (mobileMenuOpen = false)} class="text-surface-500" aria-label="Close navigation menu">
                        <X size={20} />
                    </button>
                </div>
                <nav class="space-y-1 p-4 pt-5">
                    {@render navLinks(() => (mobileMenuOpen = false))}
                </nav>
            </aside>
        </div>
    {/if}

    <!-- Main content -->
    <div class="flex flex-1 flex-col overflow-hidden">
        <!-- Top bar -->
        <header class="flex h-16 items-center justify-between border-b border-surface-200 bg-surface-50/80 px-6 backdrop-blur-xl dark:border-surface-700 dark:bg-surface-800/80">
            <button
                onclick={() => (mobileMenuOpen = !mobileMenuOpen)}
                class="rounded-lg p-2 text-surface-500 hover:bg-surface-100 dark:hover:bg-surface-700 lg:hidden"
                aria-label="Open navigation menu"
            >
                <Menu size={20} />
            </button>

            <div class="flex-1"></div>

            <div class="flex items-center gap-3">
                <TenantSwitcher {user} />
                <ThemeToggle />
                <UserMenu {user} />
            </div>
        </header>

        <!-- Page content -->
        <main class="animate-fade-in flex-1 overflow-y-auto p-6">
            {@render children()}
        </main>
    </div>
</div>
