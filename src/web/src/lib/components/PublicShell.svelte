<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { UserDto } from '$lib/api/types';

	import type { Snippet } from 'svelte';

	let { user, children }: { user: UserDto | null; children: Snippet } = $props();
</script>

<div class="min-h-screen bg-surface-50 dark:bg-surface-900">
	<header class="border-b border-surface-200 bg-surface-50/80 backdrop-blur-xl dark:border-surface-700 dark:bg-surface-900/80">
		<div class="mx-auto flex h-16 max-w-7xl items-center justify-between px-6">
			<a href="/dashboard" class="flex items-center gap-2.5">
				<div class="flex h-7 w-7 items-center justify-center rounded-lg bg-primary-500/10">
					<svg class="h-4 w-4 text-primary-500" viewBox="0 0 24 24" fill="none">
						<path d="M4.5 6 L12 19 L19.5 6" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"/>
						<circle cx="4.5" cy="6" r="2.5" fill="currentColor"/>
						<circle cx="19.5" cy="6" r="2.5" fill="currentColor"/>
						<circle cx="12" cy="19" r="2.5" fill="currentColor"/>
					</svg>
				</div>
				<span class="text-xl font-bold text-surface-900 dark:text-surface-50">VordFleet</span>
			</a>
			<nav class="flex items-center gap-4">
				{#if user}
					<a
						href="/dashboard"
						class="rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
					>
						Dashboard
					</a>
				{:else}
					<a
						href="/auth/login"
						class="rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
					>
						Sign In
					</a>
				{/if}
			</nav>
		</div>
	</header>

	<main>
		{@render children()}
	</main>

	<footer class="border-t border-surface-200 py-8 dark:border-surface-700">
		<div class="mx-auto flex max-w-7xl flex-col items-center gap-2 px-6 text-sm text-surface-500">
			<div class="flex gap-4">
				<a href="https://vordfleet.dev/support" target="_blank" rel="external noopener noreferrer" data-sveltekit-preload-data="off" class="hover:text-surface-700 dark:hover:text-surface-300">Support</a>
				<a href="/terms" class="hover:text-surface-700 dark:hover:text-surface-300">Terms of Service</a>
				<a href="/privacy" class="hover:text-surface-700 dark:hover:text-surface-300">Privacy Policy</a>
			</div>
			<span>&copy; {new Date().getFullYear()} Framlux. All rights reserved.</span>
		</div>
	</footer>
</div>
