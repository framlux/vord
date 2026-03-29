<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { InboxIcon } from 'lucide-svelte';

	let {
		title = 'No items found',
		description = ''
	}: { title?: string; description?: string } = $props();
</script>

<div class="flex flex-col items-center justify-center py-16 text-center">
	<InboxIcon size={48} class="mb-4 text-surface-400 dark:text-surface-600" />
	<h3 class="text-lg font-medium text-surface-700 dark:text-surface-300">{title}</h3>
	{#if description}
		<p class="mt-1 text-sm text-surface-500">{description}</p>
	{/if}
</div>
