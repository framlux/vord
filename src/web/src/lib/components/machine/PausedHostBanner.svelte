<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let {
		limit,
		activeRank,
		limitIsCustom,
		canManageBilling
	}: {
		limit: number;
		activeRank: number;
		limitIsCustom: boolean;
		canManageBilling: boolean;
	} = $props();

	// The plan keeps the oldest `limit` hosts active, so each older host removed moves this one a
	// place closer to the limit; it resumes when its rank reaches the limit.
	const olderToRemove = $derived(activeRank - limit);
</script>

<div
	role="status"
	class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
>
	<span>
		This host is paused. Your plan keeps your {limit} oldest hosts active, and this one is #{activeRank}.
		{#if limitIsCustom}
			Contact support to change your host limit.
		{:else}
			Remove {olderToRemove} older {olderToRemove === 1 ? 'host' : 'hosts'} you no longer use, or upgrade, to resume it.
		{/if}
	</span>
	{#if canManageBilling && limitIsCustom === false}
		<a href="/settings/billing" class="font-medium underline hover:no-underline">Billing</a>
	{/if}
</div>
