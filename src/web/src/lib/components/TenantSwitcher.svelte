<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { ChevronDown, Check } from 'lucide-svelte';
	import { ApiClient } from '$lib/api/client';
	import type { UserDto } from '$lib/api/types';

	let { user }: { user: UserDto } = $props();

	let open = $state(false);
	let switching = $state(false);

	const client = new ApiClient('');

	const activeTenant = $derived(
		user.tenants.find(t => t.tenantId === user.activeTenantId) ?? user.tenants[0] ?? null
	);

	async function switchTo(tenantId: number) {
		if (tenantId === user.activeTenantId) {
			open = false;

			return;
		}

		switching = true;
		try {
			await client.switchTenant(tenantId);
			// Invalidate the server-side session cache so the reloaded page
			// resolves roles and active tenant fresh under the new tenant.
			await fetch('/api/session/purge', { method: 'POST', credentials: 'include' });
			window.location.reload();
		} catch {
			switching = false;
		}
	}

	function handleClickOutside(event: MouseEvent) {
		const target = event.target as HTMLElement;
		if (!target.closest('.tenant-switcher')) {
			open = false;
		}
	}
</script>

<svelte:window onclick={handleClickOutside} />

{#if user.tenants.length > 1}
	<div class="tenant-switcher relative">
		<button
			onclick={() => (open = !open)}
			disabled={switching}
			class="flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium text-surface-700 transition hover:bg-surface-100 dark:text-surface-300 dark:hover:bg-surface-700"
		>
			<span class="max-w-32 truncate">{activeTenant?.tenantName ?? 'Select'}</span>
			<ChevronDown size={14} class="flex-shrink-0 text-surface-400 transition-transform {open ? 'rotate-180' : ''}" />
		</button>

		{#if open}
			<div class="absolute right-0 top-full z-50 mt-1 min-w-48 rounded-lg border border-surface-200 bg-surface-50 py-1 shadow-lg dark:border-surface-700 dark:bg-surface-800">
				{#each user.tenants as tenant}
					<button
						onclick={() => switchTo(tenant.tenantId)}
						class="flex w-full items-center gap-2 px-4 py-2 text-left text-sm transition hover:bg-surface-50 dark:hover:bg-surface-700"
					>
						{#if tenant.tenantId === user.activeTenantId}
							<Check size={14} class="flex-shrink-0 text-primary-500" />
						{:else}
							<span class="w-3.5"></span>
						{/if}
						<span class="truncate text-surface-700 dark:text-surface-300">{tenant.tenantName}</span>
					</button>
				{/each}
			</div>
		{/if}
	</div>
{:else if activeTenant}
	<span class="text-sm text-surface-500">{activeTenant.tenantName}</span>
{/if}
