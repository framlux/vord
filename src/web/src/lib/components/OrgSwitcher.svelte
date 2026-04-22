<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
    import { ChevronsUpDown, Check } from 'lucide-svelte';
    import { ApiClient } from '$lib/api/client';
    import type { UserDto, SubscriptionDto } from '$lib/api/types';

    let { user, subscription = null }: { user: UserDto; subscription?: SubscriptionDto | null } = $props();

    let open = $state(false);
    let switching = $state(false);

    const client = new ApiClient('');

    const activeTenant = $derived(
        user.tenants.find(t => t.tenantId === user.activeTenantId) ?? user.tenants[0] ?? null
    );

    const hasMultipleTenants = $derived(user.tenants.length > 1);

    function getInitials(name: string): string {
        const words = name.trim().split(/\s+/).filter(w => w.length > 0);
        if (words.length === 0) {
            return '?';
        }

        return words
            .slice(0, 2)
            .map(w => w[0])
            .join('')
            .toUpperCase();
    }

    function tierLabel(tier: string | null | undefined): string {
        if (tier === null || tier === undefined) {
            return '';
        }

        return tier.charAt(0).toUpperCase() + tier.slice(1).toLowerCase();
    }

    async function switchTo(tenantId: number) {
        if (tenantId === user.activeTenantId) {
            open = false;

            return;
        }

        switching = true;
        try {
            await client.switchTenant(tenantId);
            window.location.reload();
        } catch {
            switching = false;
        }
    }

    function handleClickOutside(event: MouseEvent) {
        const target = event.target as HTMLElement;
        if (!target.closest('.org-switcher')) {
            open = false;
        }
    }
</script>

<svelte:window onclick={handleClickOutside} />

<div class="org-switcher relative border-b border-surface-200 dark:border-surface-700">
    <button
        class="flex w-full items-center gap-2.5 px-4 py-3 text-left transition-colors hover:bg-surface-100 dark:hover:bg-surface-800"
        class:cursor-default={!hasMultipleTenants}
        onclick={() => { if (hasMultipleTenants) open = !open; }}
        disabled={switching}
        aria-label={hasMultipleTenants ? 'Switch organization' : undefined}
    >
        <div class="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-md bg-gradient-to-br from-primary-600 to-primary-500 text-[11px] font-bold text-white">
            {activeTenant ? getInitials(activeTenant.tenantName) : '?'}
        </div>
        <div class="min-w-0 flex-1">
            <div class="truncate text-[13px] font-semibold text-surface-900 dark:text-surface-50">
                {activeTenant?.tenantName ?? 'No Organization'}
            </div>
            {#if subscription?.tier}
                <div class="text-[11px] text-surface-500 dark:text-surface-400">
                    {tierLabel(subscription.tier)} Plan
                </div>
            {/if}
        </div>
        {#if hasMultipleTenants}
            <ChevronsUpDown size={14} class="flex-shrink-0 text-surface-400" />
        {/if}
    </button>

    {#if open}
        <div class="absolute left-2 right-2 top-full z-50 mt-1 rounded-lg border border-surface-200 bg-surface-50 py-1 shadow-lg dark:border-surface-700 dark:bg-surface-800">
            {#each user.tenants as tenant}
                <button
                    onclick={() => switchTo(tenant.tenantId)}
                    disabled={switching}
                    class="flex w-full items-center gap-2.5 px-3 py-2 text-left text-[13px] transition-colors hover:bg-surface-100 dark:hover:bg-surface-700"
                >
                    <div class="flex h-6 w-6 flex-shrink-0 items-center justify-center rounded text-[10px] font-bold {tenant.tenantId === user.activeTenantId ? 'bg-primary-500/15 text-primary-600 dark:text-primary-400' : 'bg-surface-200 text-surface-500 dark:bg-surface-700 dark:text-surface-400'}">
                        {getInitials(tenant.tenantName)}
                    </div>
                    <span class="flex-1 truncate text-surface-700 dark:text-surface-300">{tenant.tenantName}</span>
                    {#if tenant.tenantId === user.activeTenantId}
                        <Check size={14} class="flex-shrink-0 text-primary-500" />
                    {/if}
                </button>
            {/each}
        </div>
    {/if}
</div>
