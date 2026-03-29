<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';

	let { data } = $props();
</script>

<PublicShell user={data.user}>
	<div class="flex min-h-[60vh] items-center justify-center py-12">
		<div class="w-full max-w-lg rounded-xl border border-surface-200 bg-surface-50 p-8 text-center dark:border-surface-700 dark:bg-surface-800">
			<div class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/20">
				<svg class="h-8 w-8 text-green-600 dark:text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
					<path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7" />
				</svg>
			</div>
			<h1 class="mt-4 text-2xl font-bold text-surface-900 dark:text-surface-50">Welcome to VordFleet!</h1>
			<p class="mt-2 text-surface-500">Your organization has been set up successfully. You're ready to start managing your fleet.</p>
			<a
				href="/dashboard"
				class="mt-6 inline-block rounded-lg bg-primary-500 px-6 py-3 text-sm font-medium text-white transition hover:bg-primary-600"
			>
				Go to Dashboard
			</a>
		</div>
	</div>
</PublicShell>
