<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { commandVerb, type CommandResult } from '$lib/utils/clusterReporting';

	let { results }: { results: CommandResult[] } = $props();
</script>

{#if results.length > 0}
	<ul data-testid="command-results" class="divide-y divide-surface-100 text-sm dark:divide-surface-700">
		{#each results as result (result.machineId + result.commandType)}
			<li
				data-outcome={result.accepted ? 'accepted' : 'failed'}
				class="flex flex-wrap items-baseline gap-x-3 gap-y-1 py-2"
			>
				<a
					href="/machines/{result.machineId}"
					class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
				>
					{result.machineName}
				</a>
				<span class="text-xs uppercase tracking-wider text-surface-500 dark:text-surface-400">
					{commandVerb(result.commandType)}
				</span>
				<span
					class={result.accepted
						? 'text-green-700 dark:text-green-400'
						: 'text-red-700 dark:text-red-400'}
				>
					{result.message}
				</span>
			</li>
		{/each}
	</ul>
{/if}
