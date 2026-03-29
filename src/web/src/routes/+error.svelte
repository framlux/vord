<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { page } from '$app/state';
</script>

<div class="flex min-h-screen items-center justify-center bg-surface-50 dark:bg-surface-900">
	<div class="text-center">
		<h1 class="text-6xl font-bold text-surface-900 dark:text-surface-50">
			{page.status}
		</h1>
		<p class="mt-4 text-lg text-surface-500">
			{page.error?.message ?? 'Something went wrong'}
		</p>
		<a
			href="/dashboard"
			class="mt-8 inline-block rounded-lg bg-primary-500 px-6 py-3 text-white transition hover:bg-primary-600"
		>
			Go to Dashboard
		</a>
	</div>
</div>
