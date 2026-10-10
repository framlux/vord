<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { SubscriptionDto } from '$lib/api/types';

	let { subscription }: { subscription: SubscriptionDto | null } = $props();

	// A downgrade pauses the hosts beyond the new limit instead of deleting them, and a dunning
	// downgrade needs no customer action at all, so a tenant can have paused hosts without having
	// done anything. This is the only place that tells every user why. The server counts the paused
	// hosts, which also covers a limit of zero (none allowed) and leaves unlimited plans out.
	const pausedCount = $derived(subscription?.pausedMachineCount ?? 0);
	const overLimit = $derived(subscription !== null && pausedCount > 0);
</script>

{#if overLimit && subscription !== null}
	<div
		role="status"
		class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
	>
		<span>
			{pausedCount} {pausedCount === 1 ? 'host is' : 'hosts are'} paused: your plan keeps your {subscription.machineLimit} oldest hosts active.
			{#if subscription.machineLimitIsCustom}
				Contact support to change your host limit.
			{:else}
				Remove older hosts you no longer use, or upgrade.
			{/if}
		</span>
		<a href="/machines?status=Paused" class="font-medium underline hover:no-underline">Review paused hosts</a>
	</div>
{/if}
