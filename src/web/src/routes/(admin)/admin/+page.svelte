<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type {
		UserAccountDto,
		ServerSettingsDto,
		TenantDto,
		SettingEntry
	} from '$lib/api/types';
	import { formatDate } from '$lib/utils/format';
	import { enhance } from '$app/forms';
	import { Users, Building2, Settings, Save, CheckCircle, Clock, Activity, Server, Shield } from 'lucide-svelte';

	let { data, form } = $props();

	const users: UserAccountDto[] = $derived(data.users);
	const settings: ServerSettingsDto = $derived(data.settings);
	const tenants: TenantDto[] = $derived(data.tenants);
	const billingEnabled: boolean = $derived(data.billingEnabled);

	type TabId = 'users' | 'tenants' | 'settings';

	const tabs: { id: TabId; label: string; icon: typeof Users }[] = $derived(
		billingEnabled
			? [
					{ id: 'users', label: 'Users', icon: Users },
					{ id: 'tenants', label: 'Tenants', icon: Building2 }
				]
			: [
					{ id: 'users', label: 'Users', icon: Users },
					{ id: 'tenants', label: 'Tenants', icon: Building2 },
					{ id: 'settings', label: 'Settings', icon: Settings }
				]
	);

	let activeTab: TabId = $state('users');

	let editedValues: Record<number, string> = $state({});
	let saving = $state(false);
	let saveSuccess = $state(false);

	$effect(() => {
		const initial: Record<number, string> = {};
		for (const entry of settings.settings) {
			initial[entry.key] = entry.value;
		}
		editedValues = initial;
	});

	const hasChanges: boolean = $derived(
		settings.settings.some(
			(entry: SettingEntry) => editedValues[entry.key] !== entry.value
		)
	);

	function settingsPayload(): string {
		return JSON.stringify(
			settings.settings.map((entry: SettingEntry) => ({
				key: entry.key,
				value: editedValues[entry.key] ?? entry.value
			}))
		);
	}

	function getSetting(key: number): SettingEntry | undefined {
		return settings.settings.find((e: SettingEntry) => e.key === key);
	}

	// Setting key constants matching ServerConfigurationSettingKeys enum
	const KEYS = {
		HEARTBEAT: 1,
		CONFIG_REFRESH: 2,
		ONLINE_THRESHOLD: 3,
		CERT_EXPIRY: 4,
		TELEMETRY_CLEANUP: 5,
		DEDUP_TTL: 6,
		COMMAND_POLL: 7,
		ALLOW_SIGNUP: 8,
		COLLECT_FAST: 9,
		COLLECT_SLOW: 10,
		SEND_FAST: 11,
		SEND_SLOW: 12,
	};

	type SettingSection = {
		title: string;
		icon: typeof Clock;
		keys: number[];
	};

	const sections: SettingSection[] = [
		{ title: 'Agent Timing', icon: Clock, keys: [KEYS.HEARTBEAT, KEYS.CONFIG_REFRESH, KEYS.COMMAND_POLL] },
		{ title: 'Telemetry Collection', icon: Activity, keys: [KEYS.COLLECT_FAST, KEYS.COLLECT_SLOW] },
		{ title: 'Telemetry Transmission', icon: Activity, keys: [KEYS.SEND_FAST, KEYS.SEND_SLOW] },
		{ title: 'Server Settings', icon: Server, keys: [KEYS.ONLINE_THRESHOLD, KEYS.CERT_EXPIRY, KEYS.TELEMETRY_CLEANUP, KEYS.DEDUP_TTL] },
		{ title: 'Access Control', icon: Shield, keys: [KEYS.ALLOW_SIGNUP] },
	];

	// Human-readable labels for each setting
	const settingLabels: Record<number, string> = {
		[KEYS.HEARTBEAT]: 'Agent Heartbeat Interval',
		[KEYS.CONFIG_REFRESH]: 'Configuration Refresh Interval',
		[KEYS.COMMAND_POLL]: 'Command Poll Interval',
		[KEYS.COLLECT_FAST]: 'Fast Collection Interval',
		[KEYS.COLLECT_SLOW]: 'Slow Collection Interval',
		[KEYS.SEND_FAST]: 'Fast Send Interval',
		[KEYS.SEND_SLOW]: 'Slow Send Interval',
		[KEYS.ONLINE_THRESHOLD]: 'Online Threshold',
		[KEYS.CERT_EXPIRY]: 'Certificate Expiry Warning',
		[KEYS.TELEMETRY_CLEANUP]: 'Telemetry Cleanup Grace Period',
		[KEYS.DEDUP_TTL]: 'Deduplication TTL',
		[KEYS.ALLOW_SIGNUP]: 'Allow User Signup',
	};

	const settingUnits: Record<number, string> = {
		[KEYS.HEARTBEAT]: 'seconds',
		[KEYS.CONFIG_REFRESH]: 'seconds',
		[KEYS.COMMAND_POLL]: 'seconds',
		[KEYS.COLLECT_FAST]: 'seconds',
		[KEYS.COLLECT_SLOW]: 'seconds',
		[KEYS.SEND_FAST]: 'seconds',
		[KEYS.SEND_SLOW]: 'seconds',
		[KEYS.ONLINE_THRESHOLD]: 'seconds',
		[KEYS.CERT_EXPIRY]: 'days',
		[KEYS.TELEMETRY_CLEANUP]: 'days',
		[KEYS.DEDUP_TTL]: 'seconds',
	};
</script>

<svelte:head><title>Admin - Vord</title></svelte:head>

<div class="space-y-6">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Admin Panel</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Manage users, tenants{billingEnabled ? '' : ', and settings'}.
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

	<!-- Settings Tab (only when billing is disabled) -->
	{#if activeTab === 'settings' && billingEnabled === false}
		<form
			method="POST"
			action="?/updateSettings"
			use:enhance={() => {
				saving = true;
				saveSuccess = false;
				return async ({ update }) => {
					saving = false;
					saveSuccess = true;
					setTimeout(() => (saveSuccess = false), 3000);
					await update();
				};
			}}
		>
			<input type="hidden" name="settings" value={settingsPayload()} />

			{#if form && 'message' in form}
				<div
					class="mb-4 rounded-lg border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-800 dark:bg-red-900/20 dark:text-red-400"
				>
					{form.message}
				</div>
			{/if}

			{#if saveSuccess}
				<div
					class="mb-4 flex items-center gap-2 rounded-lg border border-green-200 bg-green-50 p-4 text-sm text-green-800 dark:border-green-800 dark:bg-green-900/20 dark:text-green-400"
				>
					<CheckCircle class="h-4 w-4" />
					Settings saved successfully.
				</div>
			{/if}

			<div class="space-y-6">
				{#each sections as section}
					{@const sectionSettings = section.keys.map(getSetting).filter((s): s is SettingEntry => s !== undefined)}
					{#if sectionSettings.length > 0}
						<div class="rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
							<div class="flex items-center gap-3 border-b border-surface-200 px-6 py-4 dark:border-surface-700">
								<section.icon class="h-5 w-5 text-surface-500 dark:text-surface-400" />
								<h3 class="text-sm font-semibold text-surface-900 dark:text-surface-100">{section.title}</h3>
							</div>
							<div class="divide-y divide-surface-100 dark:divide-surface-700">
								{#each sectionSettings as entry (entry.key)}
									<div class="flex items-center justify-between gap-8 px-6 py-4">
										<div class="min-w-0 flex-1">
											<div class="text-sm font-medium text-surface-900 dark:text-surface-100">
												{settingLabels[entry.key] ?? entry.name}
											</div>
											<div class="mt-0.5 text-xs text-surface-500 dark:text-surface-400">
												{entry.description}
											</div>
										</div>
										<div class="flex items-center gap-3">
											{#if entry.key === KEYS.ALLOW_SIGNUP}
												<select
													class="rounded-lg border border-surface-300 bg-white px-3 py-2 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
													value={editedValues[entry.key] ?? entry.value}
													onchange={(e) => {
														editedValues[entry.key] = e.currentTarget.value;
													}}
												>
													<option value="true">Enabled</option>
													<option value="false">Disabled</option>
												</select>
											{:else}
												<div class="flex items-center gap-2">
													<input
														type="number"
														min={entry.min ?? 1}
														max={entry.max ?? undefined}
														class="w-28 rounded-lg border border-surface-300 bg-white px-3 py-2 font-mono text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
														value={editedValues[entry.key] ?? entry.value}
														oninput={(e) => {
															editedValues[entry.key] = e.currentTarget.value;
														}}
													/>
													<span class="text-xs text-surface-400 dark:text-surface-500">
														{settingUnits[entry.key] ?? ''}
													</span>
												</div>
												{#if entry.min !== null && entry.max !== null}
													<span class="text-xs text-surface-400 dark:text-surface-500">
														({entry.min}&ndash;{entry.max})
													</span>
												{/if}
											{/if}
										</div>
									</div>
								{/each}
							</div>
						</div>
					{/if}
				{/each}
			</div>

			<div class="mt-6 flex items-center justify-end">
				<button
					type="submit"
					disabled={saving || hasChanges === false}
					class="inline-flex items-center gap-2 rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-700 disabled:cursor-not-allowed disabled:opacity-50"
				>
					<Save class="h-4 w-4" />
					{saving ? 'Saving...' : 'Save Changes'}
				</button>
			</div>
		</form>
	{/if}
</div>
