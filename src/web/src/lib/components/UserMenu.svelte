<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { Settings, Bell, LogOut } from 'lucide-svelte';
	import type { UserDto } from '$lib/api/types';

	let { user }: { user: UserDto } = $props();

	let open = $state(false);

	function toggle() {
		open = !open;
	}

	function close() {
		open = false;
	}

	function handleKeydown(event: KeyboardEvent) {
		if (event.key === 'Escape') {
			close();
		}
	}
</script>

<svelte:window onkeydown={handleKeydown} />

<div class="relative">
	<button
		onclick={toggle}
		class="flex items-center gap-2 rounded-lg p-1 transition hover:bg-surface-100 dark:hover:bg-surface-700"
		aria-label="User menu"
		aria-expanded={open}
	>
		<div
			class="flex h-8 w-8 items-center justify-center rounded-full bg-primary-500 text-sm font-medium text-white"
		>
			{user.name?.[0]?.toUpperCase() ?? user.email?.[0]?.toUpperCase() ?? '?'}
		</div>
		<span class="hidden text-sm font-medium text-surface-900 dark:text-surface-50 md:block">
			{user.name || user.email}
		</span>
	</button>

	{#if open}
		<!-- Backdrop -->
		<button class="fixed inset-0 z-40 cursor-default" onclick={close} tabindex="-1" aria-label="Close menu"></button>

		<!-- Dropdown -->
		<div
			class="absolute right-0 z-50 mt-2 w-56 rounded-lg border border-surface-200 bg-surface-50 shadow-lg dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="border-b border-surface-200 px-4 py-3 dark:border-surface-700">
				<p class="text-sm font-medium text-surface-900 dark:text-surface-50">
					{user.name || 'User'}
				</p>
				<p class="truncate text-xs text-surface-500">{user.email}</p>
			</div>

			<div class="py-1">
				<a
					href="/account/settings"
					onclick={close}
					class="flex items-center gap-3 px-4 py-2 text-sm text-surface-700 transition hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
				>
					<Settings size={16} />
					Settings
				</a>
				<a
					href="/account/notifications"
					onclick={close}
					class="flex items-center gap-3 px-4 py-2 text-sm text-surface-700 transition hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
				>
					<Bell size={16} />
					Notifications
				</a>
			</div>

			<div class="border-t border-surface-200 py-1 dark:border-surface-700">
				<a
					href="/auth/logout"
					onclick={close}
					class="flex items-center gap-3 px-4 py-2 text-sm text-surface-700 transition hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
				>
					<LogOut size={16} />
					Log out
				</a>
			</div>
		</div>
	{/if}
</div>
