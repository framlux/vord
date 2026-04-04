<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let {
		label,
		min = undefined,
		max = undefined,
		onchange
	}: {
		label: string;
		min: number | undefined;
		max: number | undefined;
		onchange: (min: number | undefined, max: number | undefined) => void;
	} = $props();

	// svelte-ignore state_referenced_locally
	let localMin = $state(min);
	// svelte-ignore state_referenced_locally
	let localMax = $state(max);

	function handleChange() {
		onchange(localMin, localMax);
	}
</script>

<div class="flex items-center gap-2">
	<span class="text-xs font-medium text-surface-600 dark:text-surface-400 min-w-[60px]">{label}</span>
	<input
		type="number"
		placeholder="Min"
		min="0"
		max="100"
		bind:value={localMin}
		onchange={handleChange}
		class="w-20 rounded-lg border border-surface-200 bg-surface-50 px-2 py-1.5 text-xs text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
	/>
	<span class="text-xs text-surface-400">-</span>
	<input
		type="number"
		placeholder="Max"
		min="0"
		max="100"
		bind:value={localMax}
		onchange={handleChange}
		class="w-20 rounded-lg border border-surface-200 bg-surface-50 px-2 py-1.5 text-xs text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
	/>
	<span class="text-xs text-surface-400">%</span>
</div>
