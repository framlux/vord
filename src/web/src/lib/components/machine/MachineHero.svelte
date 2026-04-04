<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { MachineDto, MachineDetailDto } from '$lib/api/types';
	import { MachineHealthStatus } from '$lib/api/types';
	import { getOsName, getTypeName } from '$lib/utils/enums';
	import { formatRelativeTime } from '$lib/utils/format';
	import HealthBadge from '$lib/components/HealthBadge.svelte';
	import MachineTypeSvg from './MachineTypeSvg.svelte';
	import { ArrowLeft } from 'lucide-svelte';

	let {
		machine,
		detail,
		isOnline,
		lastPing
	}: {
		machine: MachineDto;
		detail: MachineDetailDto | null;
		isOnline: boolean;
		lastPing: string | null;
	} = $props();

	const healthStatus = $derived(detail?.healthStatus ?? MachineHealthStatus.Offline);
</script>

<div>
	<!-- Back link -->
	<a
		href="/machines"
		class="mb-4 inline-flex items-center gap-1.5 text-sm text-surface-500 transition hover:text-primary-500 dark:text-surface-400 dark:hover:text-primary-400"
	>
		<ArrowLeft size={16} />
		Back to Machines
	</a>

	<div class="flex flex-col gap-6 lg:flex-row lg:items-start lg:justify-between">
		<!-- Left: Machine info -->
		<div class="space-y-3">
			<div class="flex items-center gap-3">
				<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">
					{machine.name}
				</h1>
				<HealthBadge status={healthStatus} />
			</div>

			{#if machine.hostname}
				<p class="font-mono text-sm text-surface-500 dark:text-surface-400">{machine.hostname}</p>
			{/if}

			<div class="flex flex-wrap items-center gap-2 text-sm">
				<span class="rounded-md bg-surface-100 px-2.5 py-1 text-surface-600 dark:bg-surface-700 dark:text-surface-300">
					{getOsName(machine.operatingSystem)}
				</span>
				<span class="rounded-md bg-surface-100 px-2.5 py-1 text-surface-600 dark:bg-surface-700 dark:text-surface-300">
					{getTypeName(machine.machineType)}
				</span>
				{#if machine.location}
					<span class="text-surface-500 dark:text-surface-400">
						{machine.location}
					</span>
				{/if}
			</div>

			<div class="flex items-center gap-2">
				{#if isOnline}
					<span class="inline-flex items-center gap-1.5 text-sm text-green-600 dark:text-green-400">
						<span class="h-2 w-2 rounded-full bg-green-500 machine-led-pulse"></span>
						Online
					</span>
				{:else}
					<span class="inline-flex items-center gap-1.5 text-sm text-surface-500 dark:text-surface-400">
						<span class="h-2 w-2 rounded-full bg-surface-400 dark:bg-surface-500"></span>
						Offline
					</span>
				{/if}
				{#if lastPing}
					<span class="text-xs text-surface-400 dark:text-surface-500">
						Last seen {formatRelativeTime(lastPing)}
					</span>
				{/if}
			</div>
		</div>

		<!-- Right: SVG visualization -->
		<div class="hidden shrink-0 lg:block">
			<MachineTypeSvg
				machineType={machine.machineType}
				{healthStatus}
				{isOnline}
				size={160}
			/>
		</div>
	</div>
</div>
