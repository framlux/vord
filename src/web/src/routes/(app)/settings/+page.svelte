<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { TenantDto, SubscriptionDto } from '$lib/api/types';
	import { Building2, Download, Check, AlertCircle, Loader2, CreditCard } from 'lucide-svelte';

	let { data } = $props();

	const tenants: TenantDto[] = $derived(data.tenants);
	const subscription: SubscriptionDto | null = $derived(data.subscription);

	function getTierBadgeClasses(tier: string): string {
		if (tier === 'Pro') {
			return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';
		}
		if (tier === 'Team') {
			return 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400';
		}

		return 'bg-surface-100 text-surface-700 dark:bg-surface-700 dark:text-surface-300';
	}

	function getStatusBadgeClasses(status: string): string {
		if (status === 'Active') {
			return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400';
		}
		if (status === 'PastDue') {
			return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400';
		}
		if (status === 'Canceled') {
			return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
		}

		return 'bg-surface-100 text-surface-700 dark:bg-surface-700 dark:text-surface-300';
	}

	function formatPeriodEnd(dateStr: string): string {
		return new Date(dateStr).toLocaleDateString('en-US', {
			month: 'long',
			day: 'numeric',
			year: 'numeric'
		});
	}

	let exportJobId = $state<number | null>(null);
	let exportStatus = $state<string | null>(null);
	let downloadUrl = $state<string | null>(null);
	let exportError = $state<string | null>(null);
	let isRequesting = $state(false);
	let pollTimer = $state<ReturnType<typeof setInterval> | null>(null);
	let fileSizeBytes = $state<number | null>(null);

	function formatFileSize(bytes: number): string {
		if (bytes < 1024) return `${bytes} B`;
		if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;

		return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
	}

	async function handleExport() {
		isRequesting = true;
		exportError = null;
		exportStatus = null;
		downloadUrl = null;
		fileSizeBytes = null;

		try {
			const response = await fetch('/settings/export', { method: 'POST' });

			if (response.status === 404) {
				exportError = 'No machine data found to export.';

				return;
			}

			if (response.ok === false) {
				exportError = 'Export request failed. Please try again.';

				return;
			}

			const data = await response.json();
			exportJobId = data.jobId;
			exportStatus = data.status;

			startPolling();
		} catch {
			exportError = 'Export request failed. Please try again.';
		} finally {
			isRequesting = false;
		}
	}

	function startPolling() {
		if (pollTimer) {
			clearInterval(pollTimer);
		}

		pollTimer = setInterval(async () => {
			if (null == exportJobId) return;

			try {
				const response = await fetch(`/settings/export?jobId=${exportJobId}`);
				if (response.ok === false) {
					exportError = 'Failed to check export status.';
					stopPolling();

					return;
				}

				const data = await response.json();
				exportStatus = data.status;

				if (data.status === 'Complete') {
					downloadUrl = data.downloadUrl;
					fileSizeBytes = data.fileSizeBytes;
					stopPolling();
				} else if (data.status === 'Failed') {
					exportError = data.errorMessage ?? 'Export failed. Please try again.';
					stopPolling();
				}
			} catch {
				exportError = 'Failed to check export status.';
				stopPolling();
			}
		}, 3000);
	}

	function stopPolling() {
		if (pollTimer) {
			clearInterval(pollTimer);
			pollTimer = null;
		}
	}

	const isExporting = $derived(
		exportStatus === 'Pending' || exportStatus === 'Processing' || isRequesting
	);
</script>

<div class="space-y-6">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Settings</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Manage tenant configuration settings.
		</p>
	</div>

	<!-- Tenants Section -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="mb-4 flex items-center gap-3">
			<Building2 class="h-5 w-5 text-surface-400 dark:text-surface-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Tenants</h2>
		</div>
		{#if tenants.length > 0}
			<ul class="divide-y divide-surface-100 dark:divide-surface-700">
				{#each tenants as tenant}
					<li class="flex items-center justify-between py-3">
						<span class="font-medium text-surface-900 dark:text-surface-100">
							{tenant.name}
						</span>
						{#if tenant.isActive}
							<span
								class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400"
							>
								Active
							</span>
						{:else}
							<span
								class="inline-flex items-center rounded-full bg-red-100 px-2.5 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/30 dark:text-red-400"
							>
								Inactive
							</span>
						{/if}
					</li>
				{/each}
			</ul>
		{:else}
			<p class="text-sm text-surface-500 dark:text-surface-400">No tenants configured.</p>
		{/if}
	</div>

	<!-- Subscription Section -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="mb-4 flex items-center gap-3">
			<CreditCard class="h-5 w-5 text-surface-400 dark:text-surface-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Subscription</h2>
		</div>
		{#if subscription === null}
			<div class="flex flex-wrap items-center gap-4">
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Tier</p>
					<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getTierBadgeClasses('Free')}">
						Free
					</span>
				</div>
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Machine Limit</p>
					<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">5 machines</p>
				</div>
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Data Retention</p>
					<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">7 day(s)</p>
				</div>
			</div>
		{:else}
			<div class="flex flex-wrap items-center gap-6">
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Tier</p>
					<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getTierBadgeClasses(subscription.tier)}">
						{subscription.tier}
					</span>
				</div>
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Status</p>
					<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getStatusBadgeClasses(subscription.status)}">
						{subscription.status}
					</span>
				</div>
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Machines</p>
					<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
						{subscription.machineCount}
						{#if subscription.machineLimit === null}
							/ Unlimited
						{:else}
							/ {subscription.machineLimit}
						{/if}
					</p>
				</div>
				<div>
					<p class="text-xs text-surface-500 dark:text-surface-400">Data Retention</p>
					<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
						{subscription.retentionDays} day(s)
					</p>
				</div>
				{#if subscription.currentPeriodEnd !== null}
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Current Period Ends</p>
						<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
							{formatPeriodEnd(subscription.currentPeriodEnd)}
						</p>
					</div>
				{/if}
			</div>
		{/if}
	</div>

	<!-- Data Export Section -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="mb-4 flex items-center gap-3">
			<Download class="h-5 w-5 text-surface-400 dark:text-surface-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Data Export</h2>
		</div>
		<p class="mb-4 text-sm text-surface-500 dark:text-surface-400">
			Export all machine-generated telemetry data as a portable SQLite database. This includes
			machine information, current state, and all telemetry history. No platform metadata
			(users, tenant configuration) is included.
		</p>
		{#if exportError}
			<div
				class="mb-4 flex items-center gap-2 rounded-lg bg-red-50 p-3 text-sm text-red-700 dark:bg-red-900/20 dark:text-red-400"
			>
				<AlertCircle class="h-4 w-4 flex-shrink-0" />
				{exportError}
			</div>
		{/if}
		{#if exportStatus === 'Pending' || exportStatus === 'Processing'}
			<div
				class="mb-4 flex items-center gap-2 rounded-lg bg-blue-50 p-3 text-sm text-blue-700 dark:bg-blue-900/20 dark:text-blue-400"
			>
				<Loader2 class="h-4 w-4 flex-shrink-0 animate-spin" />
				Preparing your export...
			</div>
		{/if}
		{#if downloadUrl}
			<div
				class="mb-4 flex items-center gap-2 rounded-lg bg-green-50 p-3 text-sm text-green-700 dark:bg-green-900/20 dark:text-green-400"
			>
				<Check class="h-4 w-4 flex-shrink-0" />
				<span>
					Download ready{fileSizeBytes ? ` (${formatFileSize(fileSizeBytes)})` : ''} —
					<a
						href={downloadUrl}
						class="font-medium underline"
						target="_blank"
						rel="noopener noreferrer"
					>
						Click to download
					</a>
				</span>
			</div>
		{/if}
		<button
			onclick={handleExport}
			disabled={isExporting}
			class="inline-flex items-center gap-2 rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-primary-700 disabled:cursor-not-allowed disabled:opacity-50 dark:bg-primary-500 dark:hover:bg-primary-600"
		>
			{#if isExporting}
				<Loader2 class="h-4 w-4 animate-spin" />
				Exporting...
			{:else}
				<Download class="h-4 w-4" />
				Export Data as SQLite
			{/if}
		</button>
	</div>
</div>
