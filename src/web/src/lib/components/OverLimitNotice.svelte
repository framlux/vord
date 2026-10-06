<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { SubscriptionDto } from '$lib/api/types';
	import { isLimited } from '$lib/utils/tier';

	let { subscription }: { subscription: SubscriptionDto | null } = $props();

	// A downgrade keeps every host, so a tenant can be over its limit without having done anything;
	// a dunning downgrade needs no customer action at all. Registration refuses new hosts until the
	// count is under the limit, and this is the only place that tells the people who would try.
	// A limit of zero means none allowed, which isLimited already distinguishes from unlimited.
	const overLimit = $derived(
		subscription !== null && isLimited(subscription.machineLimit) && subscription.machineCount > subscription.machineLimit
	);
</script>

{#if overLimit && subscription !== null}
	<div
		role="status"
		class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
	>
		<span>
			{subscription.machineCount} {subscription.machineCount === 1 ? 'host' : 'hosts'} on a {subscription.machineLimit}-host plan.
			New hosts can't be added until you remove some or upgrade.
		</span>
		<a href="/machines" class="font-medium underline hover:no-underline">Review hosts</a>
	</div>
{/if}
