<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type {
		UserAccountDto,
		ServerSettingsDto,
		TenantDto
	} from '$lib/api/types';
	import { formatDate } from '$lib/utils/format';
	import { Users, Building2, Settings } from 'lucide-svelte';

	let { data } = $props();

	const users: UserAccountDto[] = $derived(data.users);
	const settings: ServerSettingsDto = $derived(data.settings);
	const tenants: TenantDto[] = $derived(data.tenants);

	type TabId = 'users' | 'tenants' | 'settings';

	const tabs: { id: TabId; label: string; icon: typeof Users }[] = [
		{ id: 'users', label: 'Users', icon: Users },
		{ id: 'tenants', label: 'Tenants', icon: Building2 },
		{ id: 'settings', label: 'Settings', icon: Settings }
	];

	let activeTab: TabId = $state('users');
</script>

<div class="space-y-8">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Admin Panel</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Manage users, tenants, and settings.
		</p>
	</div>

	<!-- Tabs -->
	<div class="border-b border-surface-200 dark:border-surface-700">
		<nav class="-mb-px flex gap-4" aria-label="Admin tabs">
			{#each tabs as tab (tab.id)}
				<button
					onclick={() => (activeTab = tab.id)}
					class="flex items-center gap-2 border-b-2 px-1 pb-3 text-sm font-medium transition {activeTab ===
					tab.id
						? 'border-primary-500 text-primary-600 dark:text-primary-400'
						: 'border-transparent text-surface-500 hover:border-surface-300 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
				>
					<tab.icon class="h-4 w-4" />
					{tab.label}
				</button>
			{/each}
		</nav>
	</div>

	<!-- Users Tab -->
	{#if activeTab === 'users'}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Username
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								External ID
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Active
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Global Admin
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Created At
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Tenants
							</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each users as user (user.id)}
							<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4 font-medium text-surface-900 dark:text-surface-100">
									{user.username}
								</td>
								<td
									class="max-w-[200px] truncate px-6 py-4 font-mono text-xs text-surface-600 dark:text-surface-400"
								>
									{user.externalId}
								</td>
								<td class="px-6 py-4">
									{#if user.isActive}
										<span
											class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400"
											aria-label="Status: Active"
										>
											Active
										</span>
									{:else}
										<span
											class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/30 dark:text-red-400"
											aria-label="Status: Inactive"
										>
											Inactive
										</span>
									{/if}
								</td>
								<td class="px-6 py-4">
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
								</td>
								<td class="px-6 py-4 text-surface-600 dark:text-surface-400">
									{formatDate(user.createdAt)}
								</td>
								<td class="px-6 py-4 text-surface-600 dark:text-surface-400">
									{#if user.tenants.length > 0}
										{user.tenants
											.map((t) => `${t.tenantName} (${t.role})`)
											.join(', ')}
									{:else}
										<span class="text-surface-400 dark:text-surface-500">&mdash;</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
				{#if users.length === 0}
					<div class="px-6 py-8 text-center text-surface-500 dark:text-surface-400">
						No users found.
					</div>
				{/if}
			</div>
		</div>
	{/if}

	<!-- Tenants Tab -->
	{#if activeTab === 'tenants'}
		<div class="space-y-4">
			<div
				class="rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
			>
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead>
							<tr class="border-b border-surface-200 dark:border-surface-700">
								<th
									scope="col"
									class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Name
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Active
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Logo URL
								</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
							{#each tenants as tenant (tenant.id)}
								<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
									<td class="px-6 py-4 font-medium text-surface-900 dark:text-surface-100">
										{tenant.name}
									</td>
									<td class="px-6 py-4">
										{#if tenant.isActive}
											<span
												class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400"
												aria-label="Status: Active"
											>
												Active
											</span>
										{:else}
											<span
												class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/30 dark:text-red-400"
												aria-label="Status: Inactive"
											>
												Inactive
											</span>
										{/if}
									</td>
									<td
										class="max-w-[250px] truncate px-6 py-4 text-surface-600 dark:text-surface-400"
									>
										{tenant.logoUrl || '\u2014'}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
					{#if tenants.length === 0}
						<div class="px-6 py-8 text-center text-surface-500 dark:text-surface-400">
							No tenants found.
						</div>
					{/if}
				</div>
			</div>
		</div>
	{/if}

	<!-- Settings Tab -->
	{#if activeTab === 'settings'}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Key
							</th>
							<th
								scope="col"
								class="px-6 py-3 text-xs font-medium uppercase tracking-wider text-surface-500 dark:text-surface-400"
							>
								Value
							</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each settings.settings as entry (entry.key)}
							<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4 text-surface-900 dark:text-surface-100">
									{entry.key}
								</td>
								<td
									class="px-6 py-4 font-mono text-sm text-surface-600 dark:text-surface-400"
								>
									{entry.value}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
				{#if settings.settings.length === 0}
					<div class="px-6 py-8 text-center text-surface-500 dark:text-surface-400">
						No settings found.
					</div>
				{/if}
			</div>
		</div>
	{/if}
</div>
