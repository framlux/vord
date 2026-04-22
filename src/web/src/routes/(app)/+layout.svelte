<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import AppShell from '$lib/components/AppShell.svelte';

	let { data, children } = $props();
</script>

{#if data.user}
	<AppShell user={data.user} subscription={data.subscription}>
		{@render children()}
	</AppShell>
{/if}
