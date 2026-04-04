<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import VitalsGauge from './VitalsGauge.svelte';

	let {
		cpuPercent,
		memoryPercent,
		maxDiskPercent,
		diskDetails
	}: {
		cpuPercent: number | null;
		memoryPercent: number | null;
		maxDiskPercent: number | null;
		diskDetails?: string;
	} = $props();
</script>

<div class="flex gap-4">
	<div class="flex flex-1 items-center justify-center rounded-xl border border-surface-200 bg-surface-50 py-3 dark:border-surface-700 dark:bg-surface-800">
		<VitalsGauge value={cpuPercent} label="CPU" />
	</div>
	<div class="flex flex-1 items-center justify-center rounded-xl border border-surface-200 bg-surface-50 py-3 dark:border-surface-700 dark:bg-surface-800">
		<VitalsGauge value={memoryPercent} label="Memory" />
	</div>
	<div class="flex flex-1 items-center justify-center rounded-xl border border-surface-200 bg-surface-50 py-3 dark:border-surface-700 dark:bg-surface-800" title={diskDetails ?? ''}>
		<VitalsGauge value={maxDiskPercent} label="Disk" />
	</div>
</div>
