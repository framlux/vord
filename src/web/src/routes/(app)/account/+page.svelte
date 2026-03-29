<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { UserDto } from '$lib/api/types';
	import { formatDate } from '$lib/utils/format';
	import { User, Settings, Bell, ArrowRight } from 'lucide-svelte';

	let { data } = $props();

	const user: UserDto | null = $derived(data.user);
</script>

<div class="space-y-8">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Account</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Your account information and preferences.
		</p>
	</div>

	{#if user}
		<!-- User Info Card -->
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="flex items-center gap-5">
				<div
					class="flex h-16 w-16 items-center justify-center rounded-full bg-primary-500 text-2xl font-bold text-white"
				>
					{user.name?.charAt(0)?.toUpperCase() ?? '?'}
				</div>
				<div>
					<h2 class="text-xl font-bold text-surface-900 dark:text-surface-50">
						{user.name}
					</h2>
					<p class="text-surface-500 dark:text-surface-400">{user.email}</p>
				</div>
			</div>
		</div>

		<!-- Details Section -->
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<h3 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">Details</h3>
			<dl class="space-y-4">
				<div class="flex items-center justify-between">
					<dt class="text-sm font-medium text-surface-500 dark:text-surface-400">
						External ID
					</dt>
					<dd class="font-mono text-sm text-surface-900 dark:text-surface-100">
						{user.uniqueId}
					</dd>
				</div>
				<div class="flex items-center justify-between">
					<dt class="text-sm font-medium text-surface-500 dark:text-surface-400">
						Global Admin
					</dt>
					<dd>
						{#if user.isGlobalAdmin}
							<span
								class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400"
							>
								Yes
							</span>
						{:else}
							<span
								class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-600 dark:bg-surface-700 dark:text-surface-400"
							>
								No
							</span>
						{/if}
					</dd>
				</div>
				<div class="flex items-center justify-between">
					<dt class="text-sm font-medium text-surface-500 dark:text-surface-400">
						Active Since
					</dt>
					<dd class="text-sm text-surface-900 dark:text-surface-100">&mdash;</dd>
				</div>
			</dl>
		</div>

		<!-- Tenant Memberships -->
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<h3 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">
				Tenant Memberships
			</h3>
			{#if user.tenants.length > 0}
				<ul class="divide-y divide-surface-100 dark:divide-surface-700">
					{#each user.tenants as tenant}
						<li class="flex items-center justify-between py-3">
							<span class="font-medium text-surface-900 dark:text-surface-100">
								{tenant.tenantName}
							</span>
							<span
								class="inline-flex items-center rounded-full bg-primary-500/10 px-2.5 py-0.5 text-xs font-medium text-primary-700 dark:text-primary-400"
							>
								{tenant.role}
							</span>
						</li>
					{/each}
				</ul>
			{:else}
				<p class="text-sm text-surface-500 dark:text-surface-400">
					No tenant memberships found.
				</p>
			{/if}
		</div>

		<!-- Navigation Links -->
		<div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
			<a
				href="/account/settings"
				class="group flex items-center justify-between rounded-xl border border-surface-200 bg-surface-50 px-6 py-4 transition hover:border-primary-300 hover:shadow-md dark:border-surface-700 dark:bg-surface-800 dark:hover:border-primary-600"
			>
				<div class="flex items-center gap-3">
					<Settings class="h-5 w-5 text-surface-400 dark:text-surface-500" />
					<span class="font-medium text-surface-700 dark:text-surface-200">
						Account Settings
					</span>
				</div>
				<ArrowRight
					class="h-4 w-4 text-surface-300 transition group-hover:translate-x-1 group-hover:text-primary-500 dark:text-surface-600"
				/>
			</a>
			<a
				href="/account/notifications"
				class="group flex items-center justify-between rounded-xl border border-surface-200 bg-surface-50 px-6 py-4 transition hover:border-primary-300 hover:shadow-md dark:border-surface-700 dark:bg-surface-800 dark:hover:border-primary-600"
			>
				<div class="flex items-center gap-3">
					<Bell class="h-5 w-5 text-surface-400 dark:text-surface-500" />
					<span class="font-medium text-surface-700 dark:text-surface-200">
						Notifications
					</span>
				</div>
				<ArrowRight
					class="h-4 w-4 text-surface-300 transition group-hover:translate-x-1 group-hover:text-primary-500 dark:text-surface-600"
				/>
			</a>
		</div>
	{:else}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<p class="text-surface-500 dark:text-surface-400">Unable to load account information.</p>
		</div>
	{/if}
</div>
