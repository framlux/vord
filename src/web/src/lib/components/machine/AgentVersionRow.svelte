<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	let { agentVersion }: { agentVersion: string | null } = $props();

	// A machine that has never reported a version reads as "Not reported" rather than a blank
	// value, so an old agent that predates version reporting is distinguishable from an empty one.
	const display = $derived(
		agentVersion !== null && agentVersion.trim().length > 0 ? agentVersion : 'Not reported'
	);
</script>

<div class="flex justify-between">
	<span class="text-sm text-surface-500 dark:text-surface-400">Agent Version</span>
	<span
		data-testid="agent-version"
		class="font-mono text-sm text-surface-900 dark:text-surface-100"
	>
		{display}
	</span>
</div>
