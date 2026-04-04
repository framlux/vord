<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { Search, ChevronDown, ChevronUp, SlidersHorizontal } from 'lucide-svelte';
	import RangeInput from './RangeInput.svelte';
	import ThresholdInput from './ThresholdInput.svelte';
	import type { MachineSearchParams } from '$lib/api/types';

	let {
		filters,
		onapply
	}: {
		filters: MachineSearchParams;
		onapply: (filters: MachineSearchParams) => void;
	} = $props();

	let expanded = $state(true);

	// Local form state
	// svelte-ignore state_referenced_locally
	let search = $state(filters.search ?? '');
	// svelte-ignore state_referenced_locally
	let os = $state(filters.os ?? '');
	// svelte-ignore state_referenced_locally
	let type = $state(filters.type ?? '');
	// svelte-ignore state_referenced_locally
	let healthStatuses = $state<Set<string>>(parseHealthStatuses(filters.healthStatus));
	// svelte-ignore state_referenced_locally
	let cpuMin = $state<number | undefined>(filters.cpuMin);
	// svelte-ignore state_referenced_locally
	let cpuMax = $state<number | undefined>(filters.cpuMax);
	// svelte-ignore state_referenced_locally
	let memoryMin = $state<number | undefined>(filters.memoryMin);
	// svelte-ignore state_referenced_locally
	let memoryMax = $state<number | undefined>(filters.memoryMax);
	// svelte-ignore state_referenced_locally
	let diskMin = $state<number | undefined>(filters.diskMin);
	// svelte-ignore state_referenced_locally
	let diskMax = $state<number | undefined>(filters.diskMax);
	// svelte-ignore state_referenced_locally
	let pendingUpdatesMin = $state<number | undefined>(filters.pendingUpdatesMin);
	// svelte-ignore state_referenced_locally
	let securityUpdatesMin = $state<number | undefined>(filters.securityUpdatesMin);
	// svelte-ignore state_referenced_locally
	let failedServicesMin = $state<number | undefined>(filters.failedServicesMin);
	// svelte-ignore state_referenced_locally
	let hasDiskHealthIssue = $state(filters.hasDiskHealthIssue ?? false);
	// svelte-ignore state_referenced_locally
	let hasHardwareIssue = $state(filters.hasHardwareIssue ?? false);
	// svelte-ignore state_referenced_locally
	let lastSeenAfter = $state(filters.lastSeenAfter ?? '');
	// svelte-ignore state_referenced_locally
	let lastSeenBefore = $state(filters.lastSeenBefore ?? '');

	function parseHealthStatuses(value: string | undefined): Set<string> {
		if (value === undefined || value === '') {
			return new Set();
		}

		return new Set(value.split(',').filter(Boolean));
	}

	function toggleHealthStatus(status: string) {
		let next = new Set(healthStatuses);
		if (next.has(status)) {
			next.delete(status);
		} else {
			next.add(status);
		}
		healthStatuses = next;
	}

	function handleApply() {
		let result: MachineSearchParams = {};
		if (search) result.search = search;
		if (os) result.os = os;
		if (type) result.type = type;
		if (healthStatuses.size > 0) result.healthStatus = [...healthStatuses].join(',');
		if (cpuMin !== undefined) result.cpuMin = cpuMin;
		if (cpuMax !== undefined) result.cpuMax = cpuMax;
		if (memoryMin !== undefined) result.memoryMin = memoryMin;
		if (memoryMax !== undefined) result.memoryMax = memoryMax;
		if (diskMin !== undefined) result.diskMin = diskMin;
		if (diskMax !== undefined) result.diskMax = diskMax;
		if (pendingUpdatesMin !== undefined) result.pendingUpdatesMin = pendingUpdatesMin;
		if (securityUpdatesMin !== undefined) result.securityUpdatesMin = securityUpdatesMin;
		if (failedServicesMin !== undefined) result.failedServicesMin = failedServicesMin;
		if (hasDiskHealthIssue) result.hasDiskHealthIssue = true;
		if (hasHardwareIssue) result.hasHardwareIssue = true;
		if (lastSeenAfter) result.lastSeenAfter = lastSeenAfter;
		if (lastSeenBefore) result.lastSeenBefore = lastSeenBefore;
		onapply(result);
	}

	function handleClear() {
		search = '';
		os = '';
		type = '';
		healthStatuses = new Set();
		cpuMin = undefined;
		cpuMax = undefined;
		memoryMin = undefined;
		memoryMax = undefined;
		diskMin = undefined;
		diskMax = undefined;
		pendingUpdatesMin = undefined;
		securityUpdatesMin = undefined;
		failedServicesMin = undefined;
		hasDiskHealthIssue = false;
		hasHardwareIssue = false;
		lastSeenAfter = '';
		lastSeenBefore = '';
		onapply({});
	}

	function handleSearchKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter') {
			handleApply();
		}
	}

	const healthOptions = [
		{ value: 'healthy', label: 'Healthy', dot: 'bg-green-500' },
		{ value: 'warning', label: 'Warning', dot: 'bg-amber-500' },
		{ value: 'critical', label: 'Critical', dot: 'bg-red-500' },
		{ value: 'offline', label: 'Offline', dot: 'bg-gray-400' }
	];

	const osOptions = [
		{ value: '', label: 'All OS' },
		{ value: 'Windows', label: 'Windows' },
		{ value: 'MacOS', label: 'MacOS' },
		{ value: 'Ubuntu', label: 'Ubuntu' },
		{ value: 'Fedora', label: 'Fedora' },
		{ value: 'RedHat', label: 'RedHat' }
	];

	const typeOptions = [
		{ value: '', label: 'All Types' },
		{ value: 'Desktop', label: 'Desktop' },
		{ value: 'Laptop', label: 'Laptop' },
		{ value: 'BareMetalServer', label: 'Bare Metal Server' },
		{ value: 'VirtualMachine', label: 'Virtual Machine' }
	];
</script>

<div class="rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
	<!-- Header -->
	<button
		onclick={() => (expanded = !expanded)}
		class="flex w-full items-center justify-between px-4 py-3 text-left"
	>
		<div class="flex items-center gap-2">
			<SlidersHorizontal size={16} class="text-surface-500 dark:text-surface-400" />
			<span class="text-sm font-medium text-surface-700 dark:text-surface-300">Search Filters</span>
		</div>
		{#if expanded}
			<ChevronUp size={16} class="text-surface-400" />
		{:else}
			<ChevronDown size={16} class="text-surface-400" />
		{/if}
	</button>

	{#if expanded}
		<div class="space-y-4 border-t border-surface-200 px-4 py-4 dark:border-surface-700">
			<!-- Row 1: Text search + Health Status + OS + Type -->
			<div class="flex flex-wrap items-start gap-4">
				<!-- Text Search -->
				<div class="relative flex-1 min-w-[200px]">
					<Search size={16} class="absolute left-3 top-1/2 -translate-y-1/2 text-surface-400" />
					<input
						type="text"
						placeholder="Search name, hostname, model..."
						bind:value={search}
						onkeydown={handleSearchKeydown}
						class="w-full rounded-lg border border-surface-200 bg-surface-50 py-2 pl-9 pr-3 text-sm text-surface-900 placeholder-surface-400 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100 dark:placeholder-surface-500"
					/>
				</div>

				<!-- OS Filter -->
				<select
					bind:value={os}
					class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				>
					{#each osOptions as opt}
						<option value={opt.value}>{opt.label}</option>
					{/each}
				</select>

				<!-- Type Filter -->
				<select
					bind:value={type}
					class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				>
					{#each typeOptions as opt}
						<option value={opt.value}>{opt.label}</option>
					{/each}
				</select>
			</div>

			<!-- Health Status Checkboxes -->
			<div class="flex flex-wrap items-center gap-3">
				<span class="text-xs font-medium text-surface-600 dark:text-surface-400">Health:</span>
				{#each healthOptions as opt}
					<label class="flex cursor-pointer items-center gap-1.5">
						<input
							type="checkbox"
							checked={healthStatuses.has(opt.value)}
							onchange={() => toggleHealthStatus(opt.value)}
							class="rounded border-surface-300 text-primary-500 focus:ring-primary-500 dark:border-surface-600"
						/>
						<span class="h-2 w-2 rounded-full {opt.dot}" aria-hidden="true"></span>
						<span class="text-xs text-surface-700 dark:text-surface-300">{opt.label}</span>
					</label>
				{/each}
			</div>

			<!-- Row 2: Resource utilization ranges -->
			<div class="flex flex-wrap items-center gap-6">
				<RangeInput label="CPU" min={cpuMin} max={cpuMax} onchange={(min, max) => { cpuMin = min; cpuMax = max; }} />
				<RangeInput label="Memory" min={memoryMin} max={memoryMax} onchange={(min, max) => { memoryMin = min; memoryMax = max; }} />
				<RangeInput label="Disk" min={diskMin} max={diskMax} onchange={(min, max) => { diskMin = min; diskMax = max; }} />
			</div>

			<!-- Row 3: Thresholds + Toggles -->
			<div class="flex flex-wrap items-center gap-6">
				<ThresholdInput label="Pending updates" value={pendingUpdatesMin} onchange={(v) => (pendingUpdatesMin = v)} />
				<ThresholdInput label="Security updates" value={securityUpdatesMin} onchange={(v) => (securityUpdatesMin = v)} />
				<ThresholdInput label="Failed services" value={failedServicesMin} onchange={(v) => (failedServicesMin = v)} />

				<label class="flex cursor-pointer items-center gap-2">
					<input
						type="checkbox"
						bind:checked={hasDiskHealthIssue}
						class="rounded border-surface-300 text-primary-500 focus:ring-primary-500 dark:border-surface-600"
					/>
					<span class="text-xs font-medium text-surface-600 dark:text-surface-400">Disk health issues</span>
				</label>

				<label class="flex cursor-pointer items-center gap-2">
					<input
						type="checkbox"
						bind:checked={hasHardwareIssue}
						class="rounded border-surface-300 text-primary-500 focus:ring-primary-500 dark:border-surface-600"
					/>
					<span class="text-xs font-medium text-surface-600 dark:text-surface-400">Hardware issues</span>
				</label>
			</div>

			<!-- Row 4: Last seen date range -->
			<div class="flex flex-wrap items-center gap-4">
				<span class="text-xs font-medium text-surface-600 dark:text-surface-400">Last seen:</span>
				<div class="flex items-center gap-2">
					<span class="text-xs text-surface-400">after</span>
					<input
						type="datetime-local"
						bind:value={lastSeenAfter}
						class="rounded-lg border border-surface-200 bg-surface-50 px-2 py-1.5 text-xs text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
					/>
				</div>
				<div class="flex items-center gap-2">
					<span class="text-xs text-surface-400">before</span>
					<input
						type="datetime-local"
						bind:value={lastSeenBefore}
						class="rounded-lg border border-surface-200 bg-surface-50 px-2 py-1.5 text-xs text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
					/>
				</div>
			</div>

			<!-- Actions -->
			<div class="flex items-center gap-3 border-t border-surface-200 pt-3 dark:border-surface-700">
				<button
					onclick={handleApply}
					class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600"
				>
					Search
				</button>
				<button
					onclick={handleClear}
					class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 transition hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
				>
					Clear All
				</button>
			</div>
		</div>
	{/if}
</div>
