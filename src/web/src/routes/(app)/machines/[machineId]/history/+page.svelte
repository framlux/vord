<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { untrack } from 'svelte';
	import { ApiClient } from '$lib/api/client';
	import type {
		HistoryPoint,
		HistoryStats,
		DiskHistoryResponse,
		SshHistoryResponse
	} from '$lib/api/types';
	import TimeRangeBar from '$lib/components/machine/TimeRangeBar.svelte';
	import HistoryChart from '$lib/components/machine/HistoryChart.svelte';
	import SshTimeline from '$lib/components/machine/SshTimeline.svelte';
	import { ArrowLeft } from 'lucide-svelte';

	let { data } = $props();

	type TabId = 'cpu' | 'memory' | 'disk' | 'services' | 'ssh';

	const tabs: { id: TabId; label: string }[] = [
		{ id: 'cpu', label: 'CPU' },
		{ id: 'memory', label: 'Memory' },
		{ id: 'disk', label: 'Disk' },
		{ id: 'services', label: 'Services' },
		{ id: 'ssh', label: 'SSH' }
	];

	const colorMap: Record<string, string> = {
		cpu: '#7c3aed',
		memory: '#10b981',
		disk: '#3b82f6',
		services: '#ef4444'
	};

	let activeTab = $state<TabId>('cpu');
	// svelte-ignore state_referenced_locally
	let activeRange = $state(data.defaultRange);
	let loading = $state(false);

	// Chart data state
	// svelte-ignore state_referenced_locally
	let chartPoints = $state<HistoryPoint[]>(data.cpuHistory?.points ?? []);
	// svelte-ignore state_referenced_locally
	let chartStats = $state<HistoryStats | null>(data.cpuHistory?.stats ?? null);
	let chartLabel = $state('CPU Utilization');
	let chartColor = $state(colorMap.cpu);
	let chartUnit = $state('%');

	// Disk tab has multiple series
	let diskData = $state<DiskHistoryResponse | null>(null);
	let selectedDiskIndex = $state(0);

	// SSH tab data
	let sshData = $state<SshHistoryResponse | null>(null);

	let chartStepped = $state(false);
	let chartThresholds = $state(true);

	const showChart = $derived(activeTab !== 'ssh');
	const showStats = $derived(activeTab !== 'ssh' && chartStats !== null);

	async function fetchData(tab: TabId, range: string) {
		loading = true;
		const api = new ApiClient('');
		const machineId = data.machine.id;

		try {
			chartStepped = false;
			chartThresholds = tab === 'cpu' || tab === 'memory' || tab === 'disk';

			if (tab === 'cpu') {
				const resp = await api.getMachineCpuHistory(machineId, range);
				chartPoints = resp.points;
				chartStats = resp.stats;
				chartLabel = 'CPU Utilization';
				chartColor = colorMap.cpu;
				chartUnit = '%';
			} else if (tab === 'memory') {
				const resp = await api.getMachineMemoryHistory(machineId, range);
				chartPoints = resp.points;
				chartStats = resp.stats;
				chartLabel = 'Memory Usage';
				chartColor = colorMap.memory;
				chartUnit = '%';
			} else if (tab === 'disk') {
				const resp = await api.getMachineDiskHistory(machineId, range);
				diskData = resp;
				selectedDiskIndex = 0;
				if (resp.series.length > 0) {
					chartPoints = resp.series[0].points;
					chartStats = resp.series[0].stats;
				} else {
					chartPoints = [];
					chartStats = null;
				}
				chartLabel = 'Disk Usage';
				chartColor = colorMap.disk;
				chartUnit = '%';
			} else if (tab === 'services') {
				const resp = await api.getMachineServiceHistory(machineId, range);
				chartPoints = resp.points.map((p) => ({
					timestamp: p.timestamp,
					value: p.failedCount
				}));
				chartStats = resp.stats;
				chartLabel = 'Failed Services';
				chartColor = colorMap.services;
				chartUnit = '';
				chartStepped = true;
			} else if (tab === 'ssh') {
				const resp = await api.getMachineSshHistory(machineId, range);
				sshData = resp;
				chartPoints = [];
				chartStats = null;
			}
		} catch {
			chartPoints = [];
			chartStats = null;
			diskData = null;
			sshData = null;
		} finally {
			loading = false;
		}
	}

	function handleTabChange(tab: TabId) {
		activeTab = tab;
	}

	function handleRangeChange(range: string) {
		activeRange = range;
	}

	function handleDiskSeriesChange(index: number) {
		selectedDiskIndex = index;
		if (diskData !== null && diskData.series.length > index) {
			chartPoints = diskData.series[index].points;
			chartStats = diskData.series[index].stats;
		}
	}

	// Skip the first $effect run since the server already loaded the initial data.
	// Subsequent tab or range changes trigger a client-side fetch.
	let initialLoadConsumed = false;
	$effect(() => {
		const tab = activeTab;
		const range = activeRange;
		if (initialLoadConsumed == false) {
			initialLoadConsumed = true;

			return;
		}

		untrack(() => {
			fetchData(tab, range);
		});
	});
</script>

<svelte:head><title>History - {data.machine.name} - Vord</title></svelte:head>

<div class="space-y-6">
	<!-- Header -->
	<div class="flex items-center gap-3">
		<a
			href="/machines/{data.machine.id}"
			class="rounded-lg p-1.5 text-surface-500 transition hover:bg-surface-100 hover:text-surface-700 dark:text-surface-400 dark:hover:bg-surface-800 dark:hover:text-surface-200"
			title="Back to machine detail"
		>
			<ArrowLeft size={20} />
		</a>
		<div>
			<h1 class="text-xl font-bold text-surface-900 dark:text-surface-50">
				{data.machine.name}
				<span class="font-normal text-surface-400 dark:text-surface-500">/ History</span>
			</h1>
			{#if data.machine.hostname}
				<p class="text-sm text-surface-500 dark:text-surface-400">{data.machine.hostname}</p>
			{/if}
		</div>
	</div>

	<!-- Time Range Selector -->
	<TimeRangeBar
		{activeRange}
		retentionDays={data.retentionDays}
		onrangechange={handleRangeChange}
	/>

	<!-- Tab bar -->
	<div class="overflow-x-auto border-b border-surface-200 dark:border-surface-700">
		<div class="-mb-px flex gap-6" role="tablist" aria-label="History metrics">
			{#each tabs as tab}
				<button
					type="button"
					role="tab"
					aria-selected={activeTab === tab.id}
					onclick={() => handleTabChange(tab.id)}
					class="whitespace-nowrap border-b-2 px-1 py-3 text-sm font-medium transition focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary-500 focus-visible:ring-offset-2 dark:focus-visible:ring-offset-surface-900
						{activeTab === tab.id
							? 'border-primary-500 text-primary-500'
							: 'border-transparent text-surface-500 hover:border-surface-300 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
				>
					{tab.label}
				</button>
			{/each}
		</div>
	</div>

	<!-- Disk device selector (only shown on disk tab with multiple devices) -->
	{#if activeTab === 'disk' && diskData !== null && diskData.series.length > 1}
		<div class="flex items-center gap-2">
			<span class="text-sm text-surface-500 dark:text-surface-400">Device:</span>
			<div class="flex gap-1">
				{#each diskData.series as series, i}
					<button
						type="button"
						onclick={() => handleDiskSeriesChange(i)}
						class="rounded-lg px-3 py-1 text-sm font-medium transition
							{selectedDiskIndex === i
								? 'bg-blue-500 text-white'
								: 'bg-surface-100 text-surface-700 hover:bg-surface-200 dark:bg-surface-800 dark:text-surface-300 dark:hover:bg-surface-700'}"
					>
						{series.mountPoint} ({series.device})
					</button>
				{/each}
			</div>
		</div>
	{/if}

	<!-- Chart / SSH Timeline -->
	<div class="rounded-xl border border-surface-200 bg-surface-50 p-4 sm:p-6 dark:border-surface-700 dark:bg-surface-800">
		{#if loading}
			<div class="flex h-72 items-center justify-center">
				<div class="space-y-3 text-center">
					<div class="mx-auto h-8 w-8 animate-spin rounded-full border-4 border-surface-300 border-t-primary-500 dark:border-surface-600 dark:border-t-primary-400"></div>
					<p class="text-sm text-surface-500 dark:text-surface-400">Loading history...</p>
				</div>
			</div>
		{:else if showChart}
			<HistoryChart
				points={chartPoints}
				label={chartLabel}
				color={chartColor}
				unit={chartUnit}
				stepped={chartStepped}
				thresholds={chartThresholds}
			/>
		{:else if sshData !== null}
			<SshTimeline
				events={sshData.events}
				totalEvents={sshData.totalEvents}
			/>
		{:else}
			<div class="flex h-72 items-center justify-center">
				<p class="text-sm text-surface-500 dark:text-surface-400">No data available.</p>
			</div>
		{/if}
	</div>

	<!-- Stats bar (hidden for SSH) -->
	{#if showStats && chartStats !== null}
		<div class="grid grid-cols-2 gap-4 sm:grid-cols-4">
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
				<p class="text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Min</p>
				<p class="mt-1 text-lg font-bold tabular-nums text-surface-900 dark:text-surface-100">
					{chartStats.min.toFixed(1)}{chartUnit}
				</p>
			</div>
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
				<p class="text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Avg</p>
				<p class="mt-1 text-lg font-bold tabular-nums text-surface-900 dark:text-surface-100">
					{chartStats.avg.toFixed(1)}{chartUnit}
				</p>
			</div>
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
				<p class="text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Max</p>
				<p class="mt-1 text-lg font-bold tabular-nums text-surface-900 dark:text-surface-100">
					{chartStats.max.toFixed(1)}{chartUnit}
				</p>
			</div>
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
				<p class="text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">P95</p>
				<p class="mt-1 text-lg font-bold tabular-nums text-surface-900 dark:text-surface-100">
					{chartStats.p95.toFixed(1)}{chartUnit}
				</p>
			</div>
		</div>
	{/if}
</div>
