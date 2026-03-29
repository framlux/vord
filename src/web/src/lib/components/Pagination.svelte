<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { ChevronLeft, ChevronRight } from 'lucide-svelte';

	let {
		page = 1,
		totalPages = 1,
		onchange
	}: {
		page?: number;
		totalPages?: number;
		onchange?: (page: number) => void;
	} = $props();
</script>

{#if totalPages > 1}
	<div class="flex items-center gap-2">
		<button
			onclick={() => onchange?.(page - 1)}
			disabled={page <= 1}
			class="rounded-lg p-2 text-surface-500 transition hover:bg-surface-200 disabled:opacity-30 dark:hover:bg-surface-700"
		>
			<ChevronLeft size={16} />
		</button>
		<span class="text-sm text-surface-600 dark:text-surface-400">
			Page {page} of {totalPages}
		</span>
		<button
			onclick={() => onchange?.(page + 1)}
			disabled={page >= totalPages}
			class="rounded-lg p-2 text-surface-500 transition hover:bg-surface-200 disabled:opacity-30 dark:hover:bg-surface-700"
		>
			<ChevronRight size={16} />
		</button>
	</div>
{/if}
