<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import {
		DISK_WARNING_THRESHOLD,
		DISK_CRITICAL_THRESHOLD
	} from '$lib/utils/constants';

	let { value, max = 100, label = '' }: { value: number; max?: number; label?: string } = $props();

	const percent = $derived(max > 0 ? Math.max(0, Math.min(Math.round((value / max) * 100), 100)) : 0);

	const colorClass = $derived(
		percent >= DISK_CRITICAL_THRESHOLD
			? 'bg-red-500'
			: percent >= DISK_WARNING_THRESHOLD
				? 'bg-amber-500'
				: 'bg-green-500'
	);
</script>

<div class="flex items-center gap-2" role="progressbar" aria-valuenow={percent} aria-valuemin={0} aria-valuemax={100} aria-label="{label || `${percent}%`}">
	<div class="h-2 flex-1 overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700" aria-hidden="true">
		<div
			class="h-full rounded-full transition-all {colorClass}"
			style="width: {percent}%"
		></div>
	</div>
	<span class="min-w-[3ch] text-right text-xs text-surface-500 dark:text-surface-400">
		{percent}%
	</span>
	{#if label}
		<span class="text-xs text-surface-400 dark:text-surface-500">{label}</span>
	{/if}
</div>
