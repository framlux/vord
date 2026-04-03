<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import {
		LayoutDashboard,
		Monitor,
		Terminal,
		Settings,
		Shield,
		Menu,
		X,
		Users,
		Key,
		CreditCard,
		ScrollText,
		Bell
	} from 'lucide-svelte';
	import ThemeToggle from './ThemeToggle.svelte';
	import TenantSwitcher from './TenantSwitcher.svelte';
	import UserMenu from './UserMenu.svelte';
	import { page } from '$app/state';
	import { canAdminTenant, isGlobalAdmin } from '$lib/utils/roles';
	import type { UserDto } from '$lib/api/types';

	import type { Snippet } from 'svelte';

	let { user, children }: { user: UserDto; children: Snippet } = $props();

	let mobileMenuOpen = $state(false);

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
</script>

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
			<nav class="flex-1 space-y-1 overflow-y-auto p-4">
				<a href="/dashboard" class={navLinkClass('/dashboard')}>
					<LayoutDashboard size={18} />
					Dashboard
				</a>
				<a href="/machines" class={navLinkClass('/machines')}>
					<Monitor size={18} />
					Machines
				</a>
				<a href="/machines/ssh-sessions" class={navLinkClass('/machines/ssh-sessions')}>
					<Terminal size={18} />
					SSH Sessions
				</a>

				{#if canAdminTenant(user)}
					<a href="/settings" class={navLinkClass('/settings')}>
						<Settings size={18} />
						Settings
					</a>
					<a href="/settings/members" class={navLinkClass('/settings/members')}>
						<Users size={18} />
						Members
					</a>
					<a href="/settings/tokens" class={navLinkClass('/settings/tokens')}>
						<Key size={18} />
						Registration Tokens
					</a>
					<a href="/settings/signing-keys" class={navLinkClass('/settings/signing-keys')}>
						<Shield size={18} />
						Signing Keys
					</a>
					<a href="/settings/billing" class={navLinkClass('/settings/billing')}>
						<CreditCard size={18} />
						Billing
					</a>
					<a href="/settings/audit-log" class={navLinkClass('/settings/audit-log')}>
						<ScrollText size={18} />
						Audit Log
					</a>
					<a href="/settings/alerts" class={navLinkClass('/settings/alerts')}>
						<Bell size={18} />
						Alerts
					</a>
				{/if}

				{#if isGlobalAdmin(user)}
					<a href="/admin" class={navLinkClass('/admin')}>
						<Shield size={18} />
						Admin
					</a>
				{/if}
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
				<nav class="space-y-1 p-4">
					<a href="/dashboard" class={navLinkClass('/dashboard')} onclick={() => (mobileMenuOpen = false)}>
						<LayoutDashboard size={18} /> Dashboard
					</a>
					<a href="/machines" class={navLinkClass('/machines')} onclick={() => (mobileMenuOpen = false)}>
						<Monitor size={18} /> Machines
					</a>
					<a href="/machines/ssh-sessions" class={navLinkClass('/machines/ssh-sessions')} onclick={() => (mobileMenuOpen = false)}>
						<Terminal size={18} /> SSH Sessions
					</a>
					{#if canAdminTenant(user)}
						<a href="/settings" class={navLinkClass('/settings')} onclick={() => (mobileMenuOpen = false)}>
							<Settings size={18} /> Settings
						</a>
						<a href="/settings/members" class={navLinkClass('/settings/members')} onclick={() => (mobileMenuOpen = false)}>
							<Users size={18} /> Members
						</a>
						<a href="/settings/tokens" class={navLinkClass('/settings/tokens')} onclick={() => (mobileMenuOpen = false)}>
							<Key size={18} /> Registration Tokens
						</a>
						<a href="/settings/signing-keys" class={navLinkClass('/settings/signing-keys')} onclick={() => (mobileMenuOpen = false)}>
							<Shield size={18} /> Signing Keys
						</a>
						<a href="/settings/billing" class={navLinkClass('/settings/billing')} onclick={() => (mobileMenuOpen = false)}>
							<CreditCard size={18} /> Billing
						</a>
						<a href="/settings/audit-log" class={navLinkClass('/settings/audit-log')} onclick={() => (mobileMenuOpen = false)}>
							<ScrollText size={18} /> Audit Log
						</a>
						<a href="/settings/alerts" class={navLinkClass('/settings/alerts')} onclick={() => (mobileMenuOpen = false)}>
							<Bell size={18} /> Alerts
						</a>
					{/if}
					{#if isGlobalAdmin(user)}
						<a href="/admin" class={navLinkClass('/admin')} onclick={() => (mobileMenuOpen = false)}>
							<Shield size={18} /> Admin
						</a>
					{/if}
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
