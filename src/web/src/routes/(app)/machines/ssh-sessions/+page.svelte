<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { PaginatedResponse, FleetSshSessionDto } from '$lib/api/types';
	import { Terminal, ChevronLeft, ChevronRight, Search } from 'lucide-svelte';
	import { goto } from '$app/navigation';
	import { page as pageState } from '$app/state';
	import PageHeader from '$lib/components/PageHeader.svelte';

	let { data } = $props();

	const sessions: PaginatedResponse<FleetSshSessionDto> = $derived(data.sessions);

	// svelte-ignore state_referenced_locally
	let searchInput = $state(data.search);

	const actionColors: Record<string, string> = {
		connect: 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400',
		disconnect: 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-400',
		failed: 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400'
	};

	function formatTimestamp(ts: string): string {
		const date = new Date(ts);
		if (isNaN(date.getTime())) return ts;

		return date.toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: '2-digit',
			minute: '2-digit',
			second: '2-digit'
		});
	}

	function applySearch() {
		const params = new URLSearchParams();
		if (searchInput) params.set('search', searchInput);
		params.set('page', '1');

		goto(`/machines/ssh-sessions?${params.toString()}`);
	}

	function clearSearch() {
		searchInput = '';
		goto('/machines/ssh-sessions');
	}

	function goToPage(p: number) {
		const params = new URLSearchParams(pageState.url.searchParams);
		params.set('page', String(p));

		goto(`/machines/ssh-sessions?${params.toString()}`);
	}

	function handleSearchKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter') {
			applySearch();
		}
	}
</script>

<svelte:head><title>SSH Sessions - Vord</title></svelte:head>

<div class="space-y-6">
	<PageHeader title="SSH Sessions" description="Fleet-wide SSH session activity across all machines." />

	<!-- Search -->
	<div class="flex items-center gap-3">
		<div class="relative flex-1 max-w-md">
			<Search class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-surface-400" />
			<input
				type="text"
				bind:value={searchInput}
				onkeydown={handleSearchKeydown}
				placeholder="Search by machine name or user..."
				class="w-full rounded-lg border border-surface-300 bg-surface-50 py-2 pl-10 pr-3 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
			/>
		</div>
		<button
			onclick={applySearch}
			class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600"
		>
			Search
		</button>
		{#if data.search}
			<button
				onclick={clearSearch}
				class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
			>
				Clear
			</button>
		{/if}
	</div>

	<!-- Table -->
	<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
		<div class="flex items-center justify-between border-b border-surface-200 px-4 py-3 dark:border-surface-700">
			<span class="text-sm font-medium text-surface-600 dark:text-surface-300">
				SSH Sessions <span class="font-normal text-surface-400">({sessions.totalCount})</span>
			</span>
		</div>
		<div class="overflow-x-auto">
			<table class="w-full text-sm">
				<thead>
					<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Timestamp</th>
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Machine</th>
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">User</th>
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Source IP</th>
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Action</th>
						<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Auth Method</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
					{#if sessions.items.length === 0}
						<tr>
							<td colspan="6" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
								<Terminal class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
								No SSH sessions found.
							</td>
						</tr>
					{:else}
						{#each sessions.items as session, i}
							<tr class="hover:bg-surface-50 dark:hover:bg-surface-800/50 {i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''}">
								<td class="whitespace-nowrap px-4 py-3 text-surface-900 dark:text-surface-100">
									{formatTimestamp(session.timestamp)}
								</td>
								<td class="px-4 py-3">
									<a href="/machines/{session.machineId}" class="font-medium text-primary-600 hover:underline dark:text-primary-400">
										{session.machineName}
									</a>
								</td>
								<td class="px-4 py-3 font-mono text-surface-600 dark:text-surface-400">{session.user}</td>
								<td class="px-4 py-3 font-mono text-surface-600 dark:text-surface-400">{session.sourceIp}</td>
								<td class="px-4 py-3">
									<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {actionColors[session.action] ?? 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-400'}">
										{session.action}
									</span>
								</td>
								<td class="px-4 py-3 text-surface-500 dark:text-surface-400">{session.authMethod}</td>
							</tr>
						{/each}
					{/if}
				</tbody>
			</table>
		</div>

		{#if sessions.totalPages > 1}
			<div class="flex items-center justify-between border-t border-surface-200 px-4 py-3 dark:border-surface-700">
				<p class="text-sm text-surface-500 dark:text-surface-400">
					Showing {(sessions.page - 1) * sessions.pageSize + 1} to {Math.min(sessions.page * sessions.pageSize, sessions.totalCount)} of {sessions.totalCount}
				</p>
				<div class="flex items-center gap-2">
					<button
						onclick={() => goToPage(sessions.page - 1)}
						disabled={sessions.hasPreviousPage === false}
						class="rounded-lg border border-surface-300 p-1.5 text-surface-600 hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700"
					>
						<ChevronLeft class="h-4 w-4" />
					</button>
					<span class="text-sm text-surface-600 dark:text-surface-400">
						Page {sessions.page} of {sessions.totalPages}
					</span>
					<button
						onclick={() => goToPage(sessions.page + 1)}
						disabled={sessions.hasNextPage === false}
						class="rounded-lg border border-surface-300 p-1.5 text-surface-600 hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700"
					>
						<ChevronRight class="h-4 w-4" />
					</button>
				</div>
			</div>
		{/if}
	</div>
</div>
