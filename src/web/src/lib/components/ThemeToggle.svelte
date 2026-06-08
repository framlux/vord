<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { Sun, Moon } from 'lucide-svelte';
	import { getTheme, setTheme } from '$lib/stores/theme.svelte';
	import { browser } from '$app/environment';

	$effect(() => {
		if (browser) {
			const stored = document.cookie
				.split('; ')
				.find((c) => c.startsWith('framlux_theme='))
				?.split('=')[1];
			if (stored === 'dark' || stored === 'light') {
				setTheme(stored);
			} else if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
				setTheme('dark');
			} else {
				setTheme('light');
			}
		}
	});

	function toggle() {
		const next = getTheme() === 'light' ? 'dark' : 'light';
		setTheme(next);
		document.documentElement.classList.remove('dark', 'light');
		document.documentElement.classList.add(next);
		document.cookie = `framlux_theme=${next};path=/;max-age=${365 * 24 * 60 * 60};samesite=lax`;
	}
</script>

<button
	onclick={toggle}
	class="rounded-lg p-2 text-surface-500 transition hover:bg-surface-200 dark:hover:bg-surface-700"
	aria-label="Toggle theme"
>
	{#if getTheme() === 'dark'}
		<Sun size={20} />
	{:else}
		<Moon size={20} />
	{/if}
</button>
