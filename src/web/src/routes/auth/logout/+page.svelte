<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';
</script>

<PublicShell user={null}>
	<div class="flex min-h-[60vh] items-center justify-center">
		<div class="text-center">
			<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">Signed Out</h1>
			<p class="mt-2 text-surface-500">You have been signed out successfully.</p>
			<a
				href="/auth/login"
				class="mt-6 inline-block rounded-lg bg-primary-500 px-6 py-3 text-white transition hover:bg-primary-600"
			>
				Sign In
			</a>
		</div>
	</div>
</PublicShell>
