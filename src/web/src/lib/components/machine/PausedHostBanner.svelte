<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { keptHostsPhrase } from '$lib/utils/tier';

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

	// The figures come from two requests, so either can be missing (a rank of 0 stands for "not
	// loaded") or stale (a limit lowered mid-session leaves the rank inside it). A rank outside
	// the limit is the only case where "this one is #N" and "remove K older hosts" are true; in
	// every other case the banner says no more than that the host is paused.
	const hasPlace = $derived(activeRank >= 1 && olderToRemove >= 1);
	const keepsNone = $derived(limit < 1);
</script>

<div
	role="status"
	class="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
>
	<span>
		This host is paused.
		{#if hasPlace}
			{#if keepsNone}
				Your plan keeps no hosts active.
				{#if limitIsCustom}
					Contact support to change your host limit.
				{:else}
					Upgrade to resume it.
				{/if}
			{:else}
				Your plan keeps {keptHostsPhrase(limit)} active, and this one is #{activeRank}.
				{#if limitIsCustom}
					Contact support to change your host limit.
				{:else}
					Remove {olderToRemove} older {olderToRemove === 1 ? 'host' : 'hosts'} you no longer use, or upgrade, to resume it.
				{/if}
			{/if}
		{/if}
	</span>
	{#if canManageBilling && limitIsCustom === false}
		<a href="/settings/billing" class="font-medium underline hover:no-underline">Billing</a>
	{/if}
</div>
