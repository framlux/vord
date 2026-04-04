<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { X } from 'lucide-svelte';

	let {
		filters,
		onremove,
		onclear
	}: {
		filters: { key: string; label: string; value: string }[];
		onremove: (key: string) => void;
		onclear: () => void;
	} = $props();
</script>

{#if filters.length > 0}
	<div class="flex flex-wrap items-center gap-2">
		{#each filters as filter (filter.key)}
			<span
				class="inline-flex items-center gap-1.5 rounded-full border border-primary-200 bg-primary-50 px-3 py-1 text-xs font-medium text-primary-700 dark:border-primary-800 dark:bg-primary-900/20 dark:text-primary-400"
			>
				<span class="text-primary-500/70 dark:text-primary-500/50">{filter.label}:</span>
				{filter.value}
				<button
					onclick={() => onremove(filter.key)}
					class="ml-0.5 rounded-full p-0.5 transition hover:bg-primary-200 dark:hover:bg-primary-800"
					aria-label="Remove {filter.label} filter"
				>
					<X size={12} />
				</button>
			</span>
		{/each}
		<button
			onclick={onclear}
			class="text-xs font-medium text-surface-500 transition hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200"
		>
			Clear all
		</button>
	</div>
{/if}
