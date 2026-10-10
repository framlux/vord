<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { MachineHealthStatus } from '$lib/api/types';
	import HealthBadge from './HealthBadge.svelte';

	let { status, isPaused }: { status: MachineHealthStatus; isPaused: boolean } = $props();
</script>

<!-- A paused host's health is frozen at whatever it last reported, so showing it would claim a
     current state the host is not reporting. Paused replaces it everywhere. -->
{#if isPaused}
	<span
		class="inline-flex items-center gap-1.5 rounded-full bg-indigo-100 px-2.5 py-0.5 text-xs font-medium text-indigo-800 dark:bg-indigo-900/30 dark:text-indigo-300"
		aria-label="Status: Paused"
	>
		<span class="h-2 w-2 rounded-full bg-indigo-500" aria-hidden="true"></span>
		Paused
	</span>
{:else}
	<HealthBadge {status} />
{/if}
