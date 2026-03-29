<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { OperatingSystem } from '$lib/api/types';
	import { Monitor } from 'lucide-svelte';

	let { os, size = 20 }: { os: OperatingSystem; size?: number } = $props();

	const osNames: Record<number, string> = {
		[OperatingSystem.Windows]: 'Windows',
		[OperatingSystem.MacOS]: 'macOS',
		[OperatingSystem.Ubuntu]: 'Ubuntu',
		[OperatingSystem.Fedora]: 'Fedora',
		[OperatingSystem.RedHat]: 'Red Hat',
		[OperatingSystem.Unknown]: 'Unknown'
	};
</script>

<span class="inline-flex items-center gap-1.5" title={osNames[os] ?? 'Unknown'}>
	<Monitor {size} class="text-surface-400" />
	<span class="text-sm">{osNames[os] ?? 'Unknown'}</span>
</span>
