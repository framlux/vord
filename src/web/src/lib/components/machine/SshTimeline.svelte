<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { SshEvent } from '$lib/api/types';
	import { formatDateTime } from '$lib/utils/format';

	let {
		events,
		totalEvents
	}: {
		events: SshEvent[];
		totalEvents: number;
	} = $props();

	const showingSubset = $derived(totalEvents > events.length);
</script>

<div class="space-y-3">
	{#if showingSubset}
		<p class="text-sm text-surface-500 dark:text-surface-400">
			Showing {events.length} of {totalEvents.toLocaleString()} events
		</p>
	{/if}

	{#if events.length === 0}
		<div class="flex items-center justify-center py-12">
			<p class="text-sm text-surface-500 dark:text-surface-400">No SSH events in this time range.</p>
		</div>
	{:else}
		<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th scope="col" class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Timestamp</th>
							<th scope="col" class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">User</th>
							<th scope="col" class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Source IP</th>
							<th scope="col" class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Action</th>
							<th scope="col" class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Auth Method</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each events as event}
							<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
								<td class="whitespace-nowrap px-6 py-3 text-xs text-surface-700 dark:text-surface-300">{formatDateTime(event.timestamp)}</td>
								<td class="px-6 py-3 font-mono text-xs text-surface-700 dark:text-surface-300">{event.user}</td>
								<td class="px-6 py-3 font-mono text-xs text-surface-600 dark:text-surface-400">{event.sourceIp}</td>
								<td class="px-6 py-3 text-xs text-surface-600 dark:text-surface-400">{event.action}</td>
								<td class="px-6 py-3 text-xs text-surface-600 dark:text-surface-400">{event.authMethod || '---'}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}
</div>
