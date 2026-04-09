<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { onMount } from 'svelte';
	import type { PaginatedResponse, FleetMachineDto, MachineSearchParams } from '$lib/api/types';
	import { MachineHealthStatus } from '$lib/api/types';
	import { ApiClient, ApiError } from '$lib/api/client';
	import { formatRelativeTime } from '$lib/utils/format';
	import { goto } from '$app/navigation';
	import { ArrowUpDown, ChevronRight } from 'lucide-svelte';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import HealthBadge from '$lib/components/HealthBadge.svelte';
	import ProgressBar from '$lib/components/ProgressBar.svelte';
	import Pagination from '$lib/components/Pagination.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import SearchFilterPanel from '$lib/components/search/SearchFilterPanel.svelte';
	import ActiveFilterChips from '$lib/components/search/ActiveFilterChips.svelte';

	let { data } = $props();

	let pollFailures = $state(0);
	const POLL_FAILURE_THRESHOLD = 3;
	const showPollWarning = $derived(pollFailures >= POLL_FAILURE_THRESHOLD);

	let liveData = $state<PaginatedResponse<FleetMachineDto> | null>(null);

	const results: PaginatedResponse<FleetMachineDto> = $derived(liveData ?? data.results);
	const filters: MachineSearchParams = $derived(data.filters);

	// svelte-ignore state_referenced_locally
	let sortKey = $state(data.filters.sortBy ?? 'name');
	// svelte-ignore state_referenced_locally
	let sortDir = $state<'asc' | 'desc'>((data.filters.sortDir as 'asc' | 'desc') ?? 'asc');

	onMount(() => {
		const api = new ApiClient('');
		const interval = setInterval(async () => {
			try {
				const freshData = await api.searchMachines({ ...filters, sortBy: sortKey, sortDir });
				liveData = freshData;
				pollFailures = 0;
			} catch (err) {
				pollFailures += 1;
				if (err instanceof ApiError && err.status === 401) {
					window.location.href = '/auth/login';
				}
			}
		}, 30_000);

		return () => clearInterval(interval);
	});

	function applyFilters(newFilters: MachineSearchParams) {
		const url = new URL(window.location.href);
		// Clear all existing search params
		url.search = '';
		// Set new filter params
		for (const [key, value] of Object.entries(newFilters)) {
			if (value !== undefined && value !== '' && value !== false) {
				url.searchParams.set(key, String(value));
			}
		}
		if (sortKey !== 'name') url.searchParams.set('sortBy', sortKey);
		if (sortDir !== 'asc') url.searchParams.set('sortDir', sortDir);
		goto(url.toString(), { keepFocus: true });
	}

	function toggleSort(key: string) {
		if (sortKey === key) {
			sortDir = sortDir === 'asc' ? 'desc' : 'asc';
		} else {
			sortKey = key;
			sortDir = 'asc';
		}
		const url = new URL(window.location.href);
		url.searchParams.set('sortBy', sortKey);
		url.searchParams.set('sortDir', sortDir);
		url.searchParams.delete('page');
		goto(url.toString(), { keepFocus: true });
	}

	function handlePageChange(newPage: number) {
		const url = new URL(window.location.href);
		url.searchParams.set('page', String(newPage));
		goto(url.toString());
	}

	function removeFilter(key: string) {
		const url = new URL(window.location.href);
		url.searchParams.delete(key);
		url.searchParams.delete('page');
		goto(url.toString(), { keepFocus: true });
	}

	function clearAllFilters() {
		goto('/machines/search', { keepFocus: true });
	}

	function healthBorderClass(status: MachineHealthStatus): string {
		switch (status) {
			case MachineHealthStatus.Healthy:
				return 'border-l-2 border-l-green-500';
			case MachineHealthStatus.Warning:
				return 'border-l-2 border-l-amber-500';
			case MachineHealthStatus.Critical:
				return 'border-l-2 border-l-red-500';
			case MachineHealthStatus.Offline:
				return 'border-l-2 border-l-gray-300 dark:border-l-gray-600';
			default:
				return 'border-l-2 border-l-transparent';
		}
	}

	const activeFilterChips = $derived(buildFilterChips(filters));

	function buildFilterChips(f: MachineSearchParams): { key: string; label: string; value: string }[] {
		let chips: { key: string; label: string; value: string }[] = [];
		if (f.search) chips.push({ key: 'search', label: 'Search', value: f.search });
		if (f.healthStatus) chips.push({ key: 'healthStatus', label: 'Health', value: f.healthStatus });
		if (f.os) chips.push({ key: 'os', label: 'OS', value: f.os });
		if (f.type) chips.push({ key: 'type', label: 'Type', value: f.type });
		if (f.cpuMin !== undefined) chips.push({ key: 'cpuMin', label: 'CPU min', value: `${f.cpuMin}%` });
		if (f.cpuMax !== undefined) chips.push({ key: 'cpuMax', label: 'CPU max', value: `${f.cpuMax}%` });
		if (f.memoryMin !== undefined) chips.push({ key: 'memoryMin', label: 'Memory min', value: `${f.memoryMin}%` });
		if (f.memoryMax !== undefined) chips.push({ key: 'memoryMax', label: 'Memory max', value: `${f.memoryMax}%` });
		if (f.diskMin !== undefined) chips.push({ key: 'diskMin', label: 'Disk min', value: `${f.diskMin}%` });
		if (f.diskMax !== undefined) chips.push({ key: 'diskMax', label: 'Disk max', value: `${f.diskMax}%` });
		if (f.pendingUpdatesMin !== undefined) chips.push({ key: 'pendingUpdatesMin', label: 'Pending updates', value: `>= ${f.pendingUpdatesMin}` });
		if (f.securityUpdatesMin !== undefined) chips.push({ key: 'securityUpdatesMin', label: 'Security updates', value: `>= ${f.securityUpdatesMin}` });
		if (f.failedServicesMin !== undefined) chips.push({ key: 'failedServicesMin', label: 'Failed services', value: `>= ${f.failedServicesMin}` });
		if (f.hasDiskHealthIssue) chips.push({ key: 'hasDiskHealthIssue', label: 'Disk health', value: 'Issues' });
		if (f.hasHardwareIssue) chips.push({ key: 'hasHardwareIssue', label: 'Hardware', value: 'Issues' });
		if (f.lastSeenAfter) chips.push({ key: 'lastSeenAfter', label: 'Seen after', value: f.lastSeenAfter });
		if (f.lastSeenBefore) chips.push({ key: 'lastSeenBefore', label: 'Seen before', value: f.lastSeenBefore });

		return chips;
	}
</script>

<svelte:head><title>Search - Vord</title></svelte:head>

<div class="space-y-6">
	<!-- Poll failure warning -->
	{#if showPollWarning}
		<div
			class="rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
			role="alert"
		>
			Unable to refresh data. Displayed information may be outdated.
		</div>
	{/if}

	<!-- Page Header -->
	<PageHeader title="Machine Search" description="Search and filter machines by telemetry, health status, and resource utilization." />

	<!-- Filter Panel -->
	<SearchFilterPanel {filters} onapply={applyFilters} />

	<!-- Active Filter Chips -->
	<ActiveFilterChips filters={activeFilterChips} onremove={removeFilter} onclear={clearAllFilters} />

	<!-- Results Table -->
	{#if results.items.length === 0}
		<EmptyState title="No machines found" description="Try adjusting your search criteria or filters." />
	{:else}
		<div
			class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="flex items-center justify-between border-b border-surface-200 px-4 py-3 dark:border-surface-700">
				<span class="text-sm font-medium text-surface-600 dark:text-surface-300">
					Results <span class="font-normal text-surface-400">({results.totalCount})</span>
				</span>
			</div>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
						<tr>
							<th scope="col" class="px-4 py-3">
								<button
									class="flex items-center gap-1 font-medium text-surface-600 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200"
									onclick={() => toggleSort('name')}
								>
									Machine
									{#if sortKey === 'name'}
										<ArrowUpDown class="h-3.5 w-3.5" />
									{/if}
								</button>
							</th>
							<th scope="col" class="px-4 py-3">
								<button
									class="flex items-center gap-1 font-medium text-surface-600 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200"
									onclick={() => toggleSort('status')}
								>
									Status
									{#if sortKey === 'status'}
										<ArrowUpDown class="h-3.5 w-3.5" />
									{/if}
								</button>
							</th>
							<th scope="col" class="px-4 py-3">
								<button
									class="flex items-center gap-1 font-medium text-surface-600 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200"
									onclick={() => toggleSort('cpu')}
								>
									CPU
									{#if sortKey === 'cpu'}
										<ArrowUpDown class="h-3.5 w-3.5" />
									{/if}
								</button>
							</th>
							<th scope="col" class="px-4 py-3">
								<button
									class="flex items-center gap-1 font-medium text-surface-600 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200"
									onclick={() => toggleSort('memory')}
								>
									Memory
									{#if sortKey === 'memory'}
										<ArrowUpDown class="h-3.5 w-3.5" />
									{/if}
								</button>
							</th>
							<th scope="col" class="px-4 py-3">
								<button
									class="flex items-center gap-1 font-medium text-surface-600 hover:text-surface-900 dark:text-surface-400 dark:hover:text-surface-200"
									onclick={() => toggleSort('disk')}
								>
									Disk
									{#if sortKey === 'disk'}
										<ArrowUpDown class="h-3.5 w-3.5" />
									{/if}
								</button>
							</th>
							<th scope="col" class="px-4 py-3 font-medium text-surface-600 dark:text-surface-400">Issues</th>
							<th scope="col" class="px-4 py-3 font-medium text-surface-600 dark:text-surface-400">Last Seen</th>
							<th scope="col" class="w-8"></th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-800">
						{#each results.items as machine, i (machine.id)}
							<tr
								class="group transition {healthBorderClass(machine.healthStatus)} {i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-100 dark:hover:bg-surface-700/50"
							>
								<td class="px-4 py-3">
									<a href="/machines/{machine.id}" class="block">
										<p class="font-medium text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300">
											{machine.hostname ?? machine.name}
										</p>
										<p class="text-xs text-surface-400 dark:text-surface-500">
											{machine.ipAddress ?? ''}
											{#if machine.hardwareModel}
												{machine.ipAddress ? ' — ' : ''}{machine.hardwareModel}
											{/if}
										</p>
									</a>
								</td>
								<td class="px-4 py-3">
									<HealthBadge status={machine.healthStatus} />
								</td>
								<td class="w-32 px-4 py-3">
									{#if machine.cpuUsagePercent !== null}
										<ProgressBar value={machine.cpuUsagePercent} />
									{:else}
										<span class="text-xs text-surface-400">—</span>
									{/if}
								</td>
								<td class="w-32 px-4 py-3">
									{#if machine.memoryUsagePercent !== null}
										<ProgressBar value={machine.memoryUsagePercent} />
									{:else}
										<span class="text-xs text-surface-400">—</span>
									{/if}
								</td>
								<td class="w-32 px-4 py-3">
									{#if machine.maxDiskUsagePercent !== null}
										<ProgressBar value={machine.maxDiskUsagePercent} />
									{:else}
										<span class="text-xs text-surface-400">—</span>
									{/if}
								</td>
								<td class="px-4 py-3">
									<div class="flex items-center gap-2">
										{#if machine.hasDiskHealthIssue}
											<span class="rounded-full bg-red-100 px-2 py-0.5 text-[10px] font-medium text-red-700 dark:bg-red-900/30 dark:text-red-400">Disk</span>
										{/if}
										{#if machine.hasHardwareIssue}
											<span class="rounded-full bg-red-100 px-2 py-0.5 text-[10px] font-medium text-red-700 dark:bg-red-900/30 dark:text-red-400">HW</span>
										{/if}
										{#if machine.pendingUpdates > 0}
											<span class="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-medium text-amber-700 dark:bg-amber-900/30 dark:text-amber-400">{machine.pendingUpdates} updates</span>
										{/if}
										{#if machine.failedServices > 0}
											<span class="rounded-full bg-red-100 px-2 py-0.5 text-[10px] font-medium text-red-700 dark:bg-red-900/30 dark:text-red-400">{machine.failedServices} failed</span>
										{/if}
										{#if machine.hasDiskHealthIssue === false && machine.hasHardwareIssue === false && machine.pendingUpdates === 0 && machine.failedServices === 0}
											<span class="text-xs text-surface-400">—</span>
										{/if}
									</div>
								</td>
								<td class="px-4 py-3 text-surface-500 dark:text-surface-400">
									{formatRelativeTime(machine.lastPing)}
								</td>
								<td class="px-2 py-3">
									<a href="/machines/{machine.id}">
										<ChevronRight class="h-4 w-4 text-surface-300 opacity-0 transition-opacity group-hover:opacity-100" />
									</a>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>

		<!-- Pagination -->
		<div class="flex justify-center">
			<Pagination
				page={results.page}
				totalPages={results.totalPages}
				onchange={handlePageChange}
			/>
		</div>
	{/if}
</div>
