<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let {
		label,
		value = undefined,
		onchange
	}: {
		label: string;
		value: number | undefined;
		onchange: (value: number | undefined) => void;
	} = $props();

	// svelte-ignore state_referenced_locally
	let localValue = $state(value);

	function handleChange() {
		onchange(localValue);
	}
</script>

<div class="flex items-center gap-2">
	<span class="text-xs font-medium text-surface-600 dark:text-surface-400 min-w-[60px] whitespace-nowrap">{label}</span>
	<input
		type="number"
		placeholder="0"
		min="0"
		bind:value={localValue}
		onchange={handleChange}
		class="w-20 rounded-lg border border-surface-200 bg-surface-50 px-2 py-1.5 text-xs text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
	/>
</div>
