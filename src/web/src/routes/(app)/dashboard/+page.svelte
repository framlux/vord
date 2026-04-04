<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import type { PaginatedFleetOverviewDto, FleetMachineDto } from '$lib/api/types';
	import { MachineHealthStatus } from '$lib/api/types';
	import { ApiClient, ApiError } from '$lib/api/client';
	import { formatNumber, formatRelativeTime } from '$lib/utils/format';
	import HealthBadge from '$lib/components/HealthBadge.svelte';
	import ProgressBar from '$lib/components/ProgressBar.svelte';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import MachineDetailPanel from '$lib/components/MachineDetailPanel.svelte';
	import Pagination from '$lib/components/Pagination.svelte';
	import {
		Shield,
		Search,
		ArrowUpDown,
		ChevronRight
	} from 'lucide-svelte';

	let { data } = $props();

	let liveData = $state<PaginatedFleetOverviewDto | null>(null);
	const fleet: PaginatedFleetOverviewDto = $derived(liveData ?? data.fleet);

	// Track current filter/sort/page state from URL params.
	// svelte-ignore state_referenced_locally
	let searchQuery = $state(data.search ?? '');
	// svelte-ignore state_referenced_locally
	let statusFilter = $state(data.status ?? 'all');
	// svelte-ignore state_referenced_locally
	let sortKey = $state(data.sortBy ?? 'name');
	// svelte-ignore state_referenced_locally
	let sortDir = $state<'asc' | 'desc'>((data.sortDir as 'asc' | 'desc') ?? 'asc');
	// svelte-ignore state_referenced_locally
	let currentPage = $state(data.page ?? 1);

	let searchTimeout: ReturnType<typeof setTimeout> | undefined;

	function updateUrl() {
		const params = new URLSearchParams();
		if (currentPage > 1) params.set('page', currentPage.toString());
		if (searchQuery.trim()) params.set('search', searchQuery.trim());
		if (statusFilter !== 'all') params.set('status', statusFilter);
		if (sortKey !== 'name') params.set('sortBy', sortKey);
		if (sortDir !== 'asc') params.set('sortDir', sortDir);
		const qs = params.toString();
		goto(qs ? `?${qs}` : '/dashboard', { replaceState: true, noScroll: true });
	}

	function onSearchInput() {
		clearTimeout(searchTimeout);
		searchTimeout = setTimeout(() => {
			currentPage = 1;
			updateUrl();
		}, 300);
	}

	function onStatusChange() {
		currentPage = 1;
		updateUrl();
	}

	function toggleSort(key: string) {
		if (sortKey === key) {
			sortDir = sortDir === 'asc' ? 'desc' : 'asc';
		} else {
			sortKey = key;
			sortDir = 'asc';
		}
		currentPage = 1;
		updateUrl();
	}

	function onPageChange(newPage: number) {
		currentPage = newPage;
		updateUrl();
	}

	// Polling failure tracking
	let pollFailures = $state(0);
	const POLL_FAILURE_THRESHOLD = 3;
	const showPollWarning = $derived(pollFailures >= POLL_FAILURE_THRESHOLD);

	// Fleet health donut chart computations
	const donutTotal = $derived(fleet.summary.totalMachines || 1);
	const healthyCount = $derived(Math.max(0, fleet.summary.onlineMachines - fleet.summary.warningCount - fleet.summary.criticalCount));
	const healthyPct = $derived((healthyCount / donutTotal) * 100);
	const warningPct = $derived((fleet.summary.warningCount / donutTotal) * 100);
	const criticalPct = $derived((fleet.summary.criticalCount / donutTotal) * 100);
	const offlinePct = $derived((fleet.summary.offlineCount / donutTotal) * 100);
	const donutGradient = $derived(
		`conic-gradient(#10b981 0% ${healthyPct}%, #f59e0b ${healthyPct}% ${healthyPct + warningPct}%, #ef4444 ${healthyPct + warningPct}% ${healthyPct + warningPct + criticalPct}%, #9ca3af ${healthyPct + warningPct + criticalPct}% 100%)`
	);

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

	// Detail panel
	let selectedMachineId = $state<number | null>(null);

	function openDetail(machine: FleetMachineDto) {
		selectedMachineId = machine.id;
	}

	function closeDetail() {
		selectedMachineId = null;
	}

	// Polling — respects current filters/page.
	onMount(() => {
		const api = new ApiClient('');
		const interval = setInterval(async () => {
			try {
				const params: Record<string, string | number> = {
					page: currentPage,
					pageSize: 25,
					sortBy: sortKey,
					sortDir: sortDir
				};
				if (searchQuery.trim()) params.search = searchQuery.trim();
				if (statusFilter !== 'all') params.status = statusFilter;
				liveData = await api.getFleetOverview(params as Parameters<typeof api.getFleetOverview>[0]);
				pollFailures = 0;
			} catch (err) {
				pollFailures += 1;
				if (err instanceof ApiError && err.status === 401) {
					window.location.href = '/auth/login';
				}
			}
		}, 30_000);

		return () => {
			clearInterval(interval);
			clearTimeout(searchTimeout);
		};
	});
</script>

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
	<PageHeader title="Dashboard" description="Fleet overview and machine health." />

	<!-- Fleet Health Summary -->
	<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
		<div class="flex flex-col items-center gap-8 sm:flex-row">
			<!-- Donut Chart -->
			<div class="relative h-28 w-28 flex-shrink-0 rounded-full" style="background: {donutGradient}">
				<div class="absolute inset-3 flex items-center justify-center rounded-full bg-surface-50 dark:bg-surface-800">
					<div class="text-center">
						<p class="text-2xl font-bold text-surface-900 dark:text-surface-50">{formatNumber(fleet.summary.totalMachines)}</p>
						<p class="text-[10px] text-surface-400">machines</p>
					</div>
				</div>
			</div>

			<!-- Legend -->
			<div class="flex-1">
				<div class="grid grid-cols-2 gap-x-8 gap-y-2 lg:grid-cols-4">
					<div class="flex items-center gap-2.5">
						<span class="h-2.5 w-2.5 flex-shrink-0 rounded-full bg-green-500"></span>
						<div>
							<p class="text-sm font-semibold text-surface-900 dark:text-surface-50">{formatNumber(healthyCount)}</p>
							<p class="text-xs text-surface-400">Healthy ({Math.round(healthyPct)}%)</p>
						</div>
					</div>
					<div class="flex items-center gap-2.5">
						<span class="h-2.5 w-2.5 flex-shrink-0 rounded-full bg-amber-500"></span>
						<div>
							<p class="text-sm font-semibold text-surface-900 dark:text-surface-50">{formatNumber(fleet.summary.warningCount)}</p>
							<p class="text-xs text-surface-400">Warning ({Math.round(warningPct)}%)</p>
						</div>
					</div>
					<div class="flex items-center gap-2.5">
						<span class="h-2.5 w-2.5 flex-shrink-0 rounded-full bg-red-500"></span>
						<div>
							<p class="text-sm font-semibold text-surface-900 dark:text-surface-50">{formatNumber(fleet.summary.criticalCount)}</p>
							<p class="text-xs text-surface-400">Critical ({Math.round(criticalPct)}%)</p>
						</div>
					</div>
					<div class="flex items-center gap-2.5">
						<span class="h-2.5 w-2.5 flex-shrink-0 rounded-full bg-gray-400"></span>
						<div>
							<p class="text-sm font-semibold text-surface-900 dark:text-surface-50">{formatNumber(fleet.summary.offlineCount)}</p>
							<p class="text-xs text-surface-400">Offline ({Math.round(offlinePct)}%)</p>
						</div>
					</div>
				</div>
				{#if fleet.summary.securityUpdates > 0}
					<div class="mt-4 flex items-center gap-2 border-t border-surface-200 pt-3 dark:border-surface-700">
						<Shield class="h-4 w-4 text-amber-500" />
						<span class="text-sm text-surface-600 dark:text-surface-400">
							<span class="font-semibold text-surface-900 dark:text-surface-50">{formatNumber(fleet.summary.securityUpdates)}</span> security updates pending
						</span>
					</div>
				{/if}
			</div>
		</div>
	</div>

	<!-- Filter Bar -->
	<div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
		<div class="relative">
			<Search
				class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-surface-400"
			/>
			<input
				type="text"
				placeholder="Search machines..."
				bind:value={searchQuery}
				oninput={onSearchInput}
				class="w-full rounded-lg border border-surface-200 bg-surface-50 py-2 pl-10 pr-4 text-sm text-surface-900 placeholder-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-700 dark:bg-surface-800 dark:text-surface-100 dark:placeholder-surface-500 sm:w-72"
			/>
		</div>
		<div class="flex items-center gap-2">
			<select
				bind:value={statusFilter}
				onchange={onStatusChange}
				class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-700 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-700 dark:bg-surface-800 dark:text-surface-300"
			>
				<option value="all">All Status</option>
				<option value="healthy">Healthy</option>
				<option value="warning">Warning</option>
				<option value="critical">Critical</option>
				<option value="offline">Offline</option>
			</select>
		</div>
	</div>

	<!-- Fleet Table -->
	<div
		class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="flex items-center justify-between border-b border-surface-200 px-4 py-3 dark:border-surface-700">
			<span class="text-sm font-medium text-surface-600 dark:text-surface-300">
				Fleet Machines <span class="font-normal text-surface-400">({fleet.totalCount})</span>
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
						<th scope="col" class="px-4 py-3 font-medium text-surface-600 dark:text-surface-400"
							>Issues</th
						>
						<th scope="col" class="px-4 py-3 font-medium text-surface-600 dark:text-surface-400"
							>Last Seen</th
						>
						<th scope="col" class="w-8"></th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-100 dark:divide-surface-800">
					{#each fleet.machines as machine, i (machine.id)}
						<tr
							class="group cursor-pointer transition {healthBorderClass(machine.healthStatus)} {i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-100 dark:hover:bg-surface-700/50"
							onclick={() => openDetail(machine)}
						>
							<td class="px-4 py-3">
								<div>
									<p class="font-medium text-surface-900 dark:text-surface-100">
										{machine.hostname ?? machine.name}
									</p>
									<p class="text-xs text-surface-400 dark:text-surface-500">
										{machine.ipAddress ?? ''}
										{#if machine.hardwareModel}
											{machine.ipAddress ? ' — ' : ''}{machine.hardwareModel}
										{/if}
									</p>
								</div>
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
								<div class="flex items-center gap-2 text-xs">
									{#if machine.failedServices > 0}
										<span
											class="rounded bg-red-100 px-1.5 py-0.5 text-red-700 dark:bg-red-900/30 dark:text-red-400"
										>
											{machine.failedServices} svc
										</span>
									{/if}
									{#if machine.securityUpdates > 0}
										<span
											class="rounded bg-amber-100 px-1.5 py-0.5 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
										>
											{machine.securityUpdates} sec
										</span>
									{/if}
									{#if machine.hasDiskHealthIssue}
										<span
											class="rounded bg-red-100 px-1.5 py-0.5 text-red-700 dark:bg-red-900/30 dark:text-red-400"
										>
											SMART
										</span>
									{/if}
									{#if machine.hasHardwareIssue}
										<span
											class="rounded bg-red-100 px-1.5 py-0.5 text-red-700 dark:bg-red-900/30 dark:text-red-400"
										>
											HW
										</span>
									{/if}
									{#if machine.failedServices === 0 && machine.securityUpdates === 0 && machine.hasDiskHealthIssue === false && machine.hasHardwareIssue === false}
										<span class="text-surface-400">—</span>
									{/if}
								</div>
							</td>
							<td class="px-4 py-3 text-xs text-surface-500 dark:text-surface-400">
								{formatRelativeTime(machine.lastPing)}
							</td>
							<td class="px-2 py-3">
								<ChevronRight class="h-4 w-4 text-surface-300 opacity-0 transition-opacity group-hover:opacity-100" />
							</td>
						</tr>
					{:else}
						<tr>
							<td
								colspan="8"
								class="px-4 py-12 text-center text-surface-400 dark:text-surface-500"
							>
								{searchQuery || statusFilter !== 'all'
									? 'No machines match your filters.'
									: 'No machines registered yet.'}
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
			page={fleet.page}
			totalPages={fleet.totalPages}
			onchange={onPageChange}
		/>
	</div>
</div>

<!-- Detail Panel -->
<MachineDetailPanel machineId={selectedMachineId} onclose={closeDetail} />
