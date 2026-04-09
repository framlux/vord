<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { MachineDto } from '$lib/api/types';
	import { Terminal, Play, Clock, RotateCcw } from 'lucide-svelte';

	let { data } = $props();

	const machines: MachineDto[] = $derived(data.machines);

	let selectedMachineId = $state<number | null>(null);
	let queryText = $state('');
	let isExecuting = $state(false);
	let resultMessage = $state<string | null>(null);
	let queryHistory = $state<Array<{ machineId: number; machineName: string; query: string }>>([]);

	function getSelectedMachineName(): string {
		const machine = machines.find((m) => m.id === selectedMachineId);
		return machine?.name ?? '';
	}

	function executeQuery() {
		if (!selectedMachineId || !queryText.trim()) return;

		isExecuting = true;
		resultMessage = null;

		const entry = {
			machineId: selectedMachineId,
			machineName: getSelectedMachineName(),
			query: queryText.trim()
		};

		// Add to history (keep last 5)
		queryHistory = [entry, ...queryHistory.slice(0, 4)];

		// Placeholder: simulate execution
		setTimeout(() => {
			isExecuting = false;
			resultMessage =
				'Query execution will be available when the distributed query endpoints are ready.';
		}, 500);
	}

	function loadFromHistory(entry: { machineId: number; machineName: string; query: string }) {
		selectedMachineId = entry.machineId;
		queryText = entry.query;
		resultMessage = null;
	}
</script>

<svelte:head><title>Query Console - Vord</title></svelte:head>

<div class="space-y-6">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">OSQuery Console</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Execute OSQuery SQL queries against managed machines.
		</p>
	</div>

	<!-- Coming Soon Banner -->
	<div class="mb-6 rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/20">
		<p class="text-sm text-amber-800 dark:text-amber-300">
			Query execution will be available in a future update. This page is a preview of upcoming functionality.
		</p>
	</div>

	<!-- Query Form -->
	<div
		class="space-y-4 rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<!-- Machine Selector -->
		<div>
			<label
				for="machine-select"
				class="mb-1.5 block text-sm font-medium text-surface-700 dark:text-surface-300"
			>
				Target Machine
			</label>
			<select
				id="machine-select"
				bind:value={selectedMachineId}
				class="w-full rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
			>
				<option value={null}>Select a machine...</option>
				{#each machines as machine}
					<option value={machine.id}>{machine.name} ({machine.hostname})</option>
				{/each}
			</select>
		</div>

		<!-- Query Input -->
		<div>
			<label
				for="query-input"
				class="mb-1.5 block text-sm font-medium text-surface-700 dark:text-surface-300"
			>
				SQL Query
			</label>
			<textarea
				id="query-input"
				bind:value={queryText}
				rows={6}
				placeholder="SELECT * FROM system_info;"
				class="w-full rounded-lg border border-surface-200 bg-surface-50 px-4 py-3 font-mono text-sm text-surface-900 placeholder-surface-400 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-900 dark:text-surface-100 dark:placeholder-surface-500"
			></textarea>
		</div>

		<!-- Execute Button -->
		<div class="flex items-center gap-3">
			<button
				onclick={executeQuery}
				disabled
				class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:cursor-not-allowed disabled:opacity-50"
			>
				<Play size={16} />
				{isExecuting ? 'Executing...' : 'Execute Query'}
			</button>
		</div>
	</div>

	<!-- Results Area -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<h2 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">Results</h2>

		{#if resultMessage}
			<div
				class="rounded-lg border border-surface-200 bg-surface-50 p-4 text-sm text-surface-600 dark:border-surface-700 dark:bg-surface-900 dark:text-surface-400"
			>
				<div class="flex items-center gap-2">
					<Terminal size={16} class="text-surface-400" />
					<span>{resultMessage}</span>
				</div>
			</div>
		{:else}
			<div class="flex flex-col items-center justify-center py-8 text-center">
				<Terminal size={32} class="mb-2 text-surface-400 dark:text-surface-600" />
				<p class="text-sm text-surface-500 dark:text-surface-400">
					Run a query to see results.
				</p>
			</div>
		{/if}
	</div>

	<!-- Query History -->
	{#if queryHistory.length > 0}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<h2 class="mb-4 flex items-center gap-2 text-lg font-semibold text-surface-900 dark:text-surface-50">
				<Clock size={18} class="text-surface-400" />
				Query History
			</h2>

			<div class="space-y-3">
				{#each queryHistory as entry}
					<button
						onclick={() => loadFromHistory(entry)}
						class="flex w-full items-start gap-3 rounded-lg border border-surface-200 p-3 text-left transition hover:border-primary-300 hover:bg-surface-50 dark:border-surface-700 dark:hover:border-primary-600 dark:hover:bg-surface-700/50"
					>
						<RotateCcw
							size={14}
							class="mt-0.5 flex-shrink-0 text-surface-400 dark:text-surface-500"
						/>
						<div class="min-w-0 flex-1">
							<p class="text-xs font-medium text-surface-500 dark:text-surface-400">
								{entry.machineName}
							</p>
							<p
								class="mt-0.5 truncate font-mono text-sm text-surface-700 dark:text-surface-300"
							>
								{entry.query}
							</p>
						</div>
					</button>
				{/each}
			</div>
		</div>
	{/if}
</div>
