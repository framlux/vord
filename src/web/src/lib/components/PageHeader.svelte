<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let { title, description = '' }: { title: string; description?: string } = $props();
</script>

<div>
	<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">{title}</h1>
	{#if description}
		<p class="mt-1 text-sm text-surface-500 dark:text-surface-400">{description}</p>
	{/if}
</div>
