<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let { online, lastPing }: { online: boolean; lastPing?: string | null } = $props();

	import { formatRelativeTime } from '$lib/utils/format';

	const statusLabel = $derived(online ? 'Online' : 'Offline');
</script>

<span
	class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium {online
		? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400'
		: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400'}"
	aria-label="Status: {statusLabel}"
>
	<span
		class="h-2 w-2 rounded-full {online ? 'bg-green-500' : 'bg-red-500'}"
		aria-hidden="true"
	></span>
	{statusLabel}
	{#if lastPing}
		<span class="text-xs opacity-60">({formatRelativeTime(lastPing)})</span>
	{/if}
</span>
