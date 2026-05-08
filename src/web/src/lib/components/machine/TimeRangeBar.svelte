<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { Lock } from 'lucide-svelte';

	let {
		activeRange,
		retentionDays,
		onrangechange
	}: {
		activeRange: string;
		retentionDays: number;
		onrangechange: (range: string) => void;
	} = $props();

	const ranges = [
		{ value: '1h', label: '1h', requiredDays: 0.042 },
		{ value: '6h', label: '6h', requiredDays: 0.25 },
		{ value: '24h', label: '24h', requiredDays: 1 },
		{ value: '7d', label: '7d', requiredDays: 7 },
		{ value: '30d', label: '30d', requiredDays: 30 }
	];

	function upgradeTierName(requiredDays: number): string {
		if (requiredDays > 60) {
			return 'Team';
		}

		return 'Pro';
	}
</script>

<div class="flex items-center gap-1" role="group" aria-label="Time range">
	{#each ranges as range}
		{@const isDisabled = retentionDays < range.requiredDays}
		{@const isActive = activeRange === range.value}
		<button
			type="button"
			disabled={isDisabled}
			title={isDisabled ? `Upgrade to ${upgradeTierName(range.requiredDays)} for ${range.label} history` : `Show ${range.label} of history`}
			onclick={() => onrangechange(range.value)}
			class="inline-flex items-center gap-1 rounded-lg px-3 py-1.5 text-sm font-medium transition
				{isActive
					? 'bg-primary-500 text-white'
					: isDisabled
						? 'cursor-not-allowed bg-surface-100 text-surface-400 dark:bg-surface-800 dark:text-surface-600'
						: 'bg-surface-100 text-surface-700 hover:bg-surface-200 dark:bg-surface-800 dark:text-surface-300 dark:hover:bg-surface-700'}"
		>
			{range.label}
			{#if isDisabled}
				<Lock size={12} />
			{/if}
		</button>
	{/each}
</div>
