<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { env } from '$env/dynamic/public';

	let { machineCount, machineLimit }: { machineCount: number; machineLimit: number | null } = $props();

	let atLimit = $derived(machineLimit !== null && machineCount >= machineLimit);
	let nearLimit = $derived(machineLimit !== null && machineCount >= machineLimit - 1 && !atLimit);

	const billingUrl = env.PUBLIC_BILLING_URL ?? '';
	const upgradeHref = billingUrl !== '' ? `${billingUrl}/upgrade` : '/settings/billing';
</script>

{#if atLimit}
	<div class="rounded-lg border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-900/20">
		<div class="flex items-start gap-3">
			<svg class="mt-0.5 h-5 w-5 flex-shrink-0 text-amber-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
				<path stroke-linecap="round" stroke-linejoin="round" d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
			</svg>
			<div>
				<p class="text-sm font-medium text-amber-800 dark:text-amber-200">
					You've reached {machineCount} of {machineLimit} machines on Free.
				</p>
				<p class="mt-1 text-sm text-amber-700 dark:text-amber-300">
					Upgrade to Pro for unlimited machines — $3/host/month.
				</p>
				<a
					href={upgradeHref}
					class="mt-2 inline-block text-sm font-medium text-amber-800 underline hover:no-underline dark:text-amber-200"
				>
					Upgrade Now
				</a>
			</div>
		</div>
	</div>
{:else if nearLimit}
	<div class="rounded-lg border border-blue-200 bg-blue-50 p-4 dark:border-blue-800 dark:bg-blue-900/20">
		<p class="text-sm text-blue-700 dark:text-blue-300">
			You're using {machineCount} of {machineLimit} machines on Free.
			<a href={upgradeHref} class="font-medium underline hover:no-underline">Upgrade to Pro</a> for unlimited machines.
		</p>
	</div>
{/if}
