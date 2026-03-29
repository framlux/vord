<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { MachineHealthStatus } from '$lib/api/types';

	let { status }: { status: MachineHealthStatus } = $props();

	const config = $derived(
		status === MachineHealthStatus.Healthy
			? {
					label: 'Healthy',
					dot: 'bg-green-500',
					bg: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400'
				}
			: status === MachineHealthStatus.Warning
				? {
						label: 'Warning',
						dot: 'bg-amber-500',
						bg: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400'
					}
				: status === MachineHealthStatus.Critical
					? {
							label: 'Critical',
							dot: 'bg-red-500',
							bg: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400'
						}
					: {
							label: 'Offline',
							dot: 'bg-gray-400',
							bg: 'bg-gray-100 text-gray-600 dark:bg-gray-800/50 dark:text-gray-400'
						}
	);
</script>

<span
	class="inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium {config.bg}"
	aria-label="Status: {config.label}"
>
	<span class="h-2 w-2 rounded-full {config.dot}" aria-hidden="true"></span>
	{config.label}
</span>
