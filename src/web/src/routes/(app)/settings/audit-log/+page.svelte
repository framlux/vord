<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { PaginatedResponse, AuditLogEntryDto } from '$lib/api/types';
	import { ScrollText, ChevronLeft, ChevronRight, AlertCircle } from 'lucide-svelte';
	import { goto } from '$app/navigation';
	import { page as pageStore } from '$app/state';
	import PageHeader from '$lib/components/PageHeader.svelte';

	let { data } = $props();

	const auditLog: PaginatedResponse<AuditLogEntryDto> | null = $derived(data.auditLog);
	const filters = $derived(data.filters);

	// svelte-ignore state_referenced_locally
	let actionFilter = $state(filters.action ?? '');
	// svelte-ignore state_referenced_locally
	let fromFilter = $state(filters.from ?? '');
	// svelte-ignore state_referenced_locally
	let toFilter = $state(filters.to ?? '');

	const actionLabels: Record<string, string> = {
		UserLogin: 'User Login',
		UserLogout: 'User Logout',
		MemberInvited: 'Member Invited',
		MemberInvitationAccepted: 'Invitation Accepted',
		MemberInvitationRevoked: 'Invitation Revoked',
		MemberRemoved: 'Member Removed',
		MemberRoleChanged: 'Role Changed',
		MachineRegistered: 'Machine Registered',
		MachineDeleted: 'Machine Deleted',
		TenantCreated: 'Tenant Created',
		TenantSettingsChanged: 'Settings Changed',
		SubscriptionUpgraded: 'Subscription Upgraded',
		SubscriptionDowngraded: 'Subscription Downgraded',
		SubscriptionCanceled: 'Subscription Canceled',
		RegistrationTokenCreated: 'Token Created',
		RegistrationTokenRevoked: 'Token Revoked',
		DataExportRequested: 'Data Export',
	};

	const allActions = Object.keys(actionLabels);

	function getActionLabel(action: string): string {
		return actionLabels[action] ?? action;
	}

	function formatTimestamp(ts: string): string {
		return new Date(ts).toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: '2-digit',
			minute: '2-digit',
			second: '2-digit',
		});
	}

	function applyFilters() {
		const params = new URLSearchParams();
		if (actionFilter) params.set('action', actionFilter);
		if (fromFilter) params.set('from', fromFilter);
		if (toFilter) params.set('to', toFilter);
		params.set('page', '1');

		goto(`/settings/audit-log?${params.toString()}`);
	}

	function clearFilters() {
		actionFilter = '';
		fromFilter = '';
		toFilter = '';
		goto('/settings/audit-log');
	}

	function goToPage(p: number) {
		const params = new URLSearchParams(pageStore.url.searchParams);
		params.set('page', String(p));

		goto(`/settings/audit-log?${params.toString()}`);
	}
</script>

<div class="space-y-6">
	<!-- Page Header -->
	<PageHeader title="Audit Log" description="View a record of all actions performed in your organization." />

	{#if auditLog === null}
		<div
			class="flex items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 p-6 dark:border-amber-800 dark:bg-amber-900/20"
		>
			<AlertCircle class="h-5 w-5 text-amber-600 dark:text-amber-400" />
			<p class="text-sm text-amber-700 dark:text-amber-300">
				The audit log is available on the Team plan. Upgrade your subscription to access this feature.
			</p>
		</div>
	{:else}
		<!-- Filters -->
		<div
			class="flex flex-wrap items-end gap-4 rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800"
		>
			<div>
				<label for="action-filter" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Action</label>
				<select
					id="action-filter"
					bind:value={actionFilter}
					class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-1.5 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				>
					<option value="">All actions</option>
					{#each allActions as action}
						<option value={action}>{getActionLabel(action)}</option>
					{/each}
				</select>
			</div>
			<div>
				<label for="from-filter" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">From</label>
				<input
					id="from-filter"
					type="date"
					bind:value={fromFilter}
					class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-1.5 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				/>
			</div>
			<div>
				<label for="to-filter" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">To</label>
				<input
					id="to-filter"
					type="date"
					bind:value={toFilter}
					class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-1.5 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				/>
			</div>
			<button
				onclick={applyFilters}
				class="rounded-lg bg-primary-600 px-4 py-1.5 text-sm font-medium text-white transition-colors hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600"
			>
				Filter
			</button>
			<button
				onclick={clearFilters}
				class="rounded-lg border border-surface-300 px-4 py-1.5 text-sm font-medium text-surface-700 transition-colors hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
			>
				Clear
			</button>
		</div>

		<!-- Table -->
		<div
			class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="flex items-center justify-between border-b border-surface-200 px-4 py-3 dark:border-surface-700">
				<span class="text-sm font-medium text-surface-600 dark:text-surface-300">
					Audit Log <span class="font-normal text-surface-400">({auditLog.totalCount})</span>
				</span>
			</div>
			<div class="overflow-x-auto">
				<table class="w-full text-sm">
					<thead>
						<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
							<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Timestamp</th>
							<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">User</th>
							<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Action</th>
							<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Resource</th>
							<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">IP Address</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#if auditLog.items.length === 0}
							<tr>
								<td colspan="5" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
									<ScrollText class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
									No audit log entries found.
								</td>
							</tr>
						{:else}
							{#each auditLog.items as entry, i}
								<tr class="hover:bg-surface-50 dark:hover:bg-surface-800/50 {i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''}">
									<td class="whitespace-nowrap px-4 py-3 text-surface-900 dark:text-surface-100">
										{formatTimestamp(entry.timestamp)}
									</td>
									<td class="px-4 py-3 text-surface-600 dark:text-surface-400">
										{entry.userEmail ?? 'System'}
									</td>
									<td class="px-4 py-3">
										<span class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-700 dark:bg-surface-700 dark:text-surface-300">
											{getActionLabel(entry.action)}
										</span>
									</td>
									<td class="px-4 py-3 text-surface-600 dark:text-surface-400">
										{entry.resourceType}
										{#if entry.resourceId}
											<span class="text-surface-400 dark:text-surface-500">#{entry.resourceId}</span>
										{/if}
									</td>
									<td class="px-4 py-3 text-surface-500 dark:text-surface-400">
										{entry.ipAddress ?? '-'}
									</td>
								</tr>
							{/each}
						{/if}
					</tbody>
				</table>
			</div>

			<!-- Pagination -->
			{#if auditLog.totalPages > 1}
				<div class="flex items-center justify-between border-t border-surface-200 px-4 py-3 dark:border-surface-700">
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Showing {(auditLog.page - 1) * auditLog.pageSize + 1} to {Math.min(auditLog.page * auditLog.pageSize, auditLog.totalCount)} of {auditLog.totalCount}
					</p>
					<div class="flex items-center gap-2">
						<button
							onclick={() => goToPage(auditLog.page - 1)}
							disabled={auditLog.hasPreviousPage === false}
							class="rounded-lg border border-surface-300 p-1.5 text-surface-600 transition-colors hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700"
						>
							<ChevronLeft class="h-4 w-4" />
						</button>
						<span class="text-sm text-surface-600 dark:text-surface-400">
							Page {auditLog.page} of {auditLog.totalPages}
						</span>
						<button
							onclick={() => goToPage(auditLog.page + 1)}
							disabled={auditLog.hasNextPage === false}
							class="rounded-lg border border-surface-300 p-1.5 text-surface-600 transition-colors hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700"
						>
							<ChevronRight class="h-4 w-4" />
						</button>
					</div>
				</div>
			{/if}
		</div>
	{/if}
</div>
