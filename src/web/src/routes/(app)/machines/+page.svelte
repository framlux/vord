<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
    import { onMount } from 'svelte';
    import type { PaginatedResponse, MachineDto } from '$lib/api/types';
    import { OperatingSystem, MachineType } from '$lib/api/types';
    import { formatRelativeTime } from '$lib/utils/format';
    import { getOsName, getTypeName } from '$lib/utils/enums';
    import Pagination from '$lib/components/Pagination.svelte';
    import EmptyState from '$lib/components/EmptyState.svelte';
    import { goto, invalidateAll } from '$app/navigation';
    import { Search, ChevronRight } from 'lucide-svelte';
    import { canAdminMachines } from '$lib/utils/roles';

    let { data } = $props();

    let pollFailures = $state(0);
    const POLL_FAILURE_THRESHOLD = 3;
    const showPollWarning = $derived(pollFailures >= POLL_FAILURE_THRESHOLD);

    onMount(() => {
        const interval = setInterval(async () => {
            try {
                await invalidateAll();
                pollFailures = 0;
            } catch {
                pollFailures += 1;
            }
        }, 30_000);
        return () => clearInterval(interval);
    });

    const machines: PaginatedResponse<MachineDto> = $derived(data.machines);
    const filters = $derived(data.filters);

    // svelte-ignore state_referenced_locally
    let searchValue = $state(filters.search);

    const osOptions = [
        { value: '', label: 'All OS' },
        { value: 'Unknown', label: 'Unknown' },
        { value: 'Windows', label: 'Windows' },
        { value: 'MacOS', label: 'MacOS' },
        { value: 'Ubuntu', label: 'Ubuntu' },
        { value: 'Fedora', label: 'Fedora' },
        { value: 'RedHat', label: 'RedHat' }
    ];

    const typeOptions = [
        { value: '', label: 'All Types' },
        { value: 'Unknown', label: 'Unknown' },
        { value: 'Desktop', label: 'Desktop' },
        { value: 'Laptop', label: 'Laptop' },
        { value: 'BareMetalServer', label: 'Bare Metal Server' },
        { value: 'VirtualMachine', label: 'Virtual Machine' }
    ];

    const statusOptions = [
        { value: '', label: 'All Status' },
        { value: 'Online', label: 'Online' },
        { value: 'Offline', label: 'Offline' }
    ];

    function updateFilters(updates: Record<string, string>) {
        const url = new URL(window.location.href);
        for (const [key, value] of Object.entries(updates)) {
            if (value) {
                url.searchParams.set(key, value);
            } else {
                url.searchParams.delete(key);
            }
        }
        url.searchParams.delete('page');
        goto(url.toString(), { keepFocus: true });
    }

    function handleSearch() {
        updateFilters({ search: searchValue });
    }

    function handleSearchKeydown(e: KeyboardEvent) {
        if (e.key === 'Enter') {
            handleSearch();
        }
    }

    function handlePageChange(newPage: number) {
        const url = new URL(window.location.href);
        url.searchParams.set('page', String(newPage));
        goto(url.toString());
    }
</script>

<svelte:head><title>Machines - Vord</title></svelte:head>

<div class="space-y-6">
    <!-- Poll failure warning -->
    {#if showPollWarning}
        <div
            class="rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
            role="alert"
        >
            Unable to refresh data. Displayed information may be outdated.
        </div>
    {/if}

    <!-- Page Header -->
    <div class="flex items-center gap-3">
        <h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Machines</h1>
        <span
            class="rounded-full bg-primary-500/10 px-3 py-0.5 text-sm font-medium text-primary-500"
        >
            {machines.totalCount}
        </span>
    </div>

    <!-- Filter Bar -->
    <div
        class="flex flex-wrap items-center gap-3 rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800"
    >
        <!-- Search -->
        <div class="relative flex-1 min-w-[200px]">
            <Search
                size={16}
                class="absolute left-3 top-1/2 -translate-y-1/2 text-surface-400"
            />
            <input
                type="text"
                placeholder="Search machines..."
                aria-label="Search machines"
                bind:value={searchValue}
                onkeydown={handleSearchKeydown}
                onblur={handleSearch}
                class="w-full rounded-lg border border-surface-200 bg-surface-50 py-2 pl-9 pr-3 text-sm text-surface-900 placeholder-surface-400 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100 dark:placeholder-surface-500"
            />
        </div>

        <!-- OS Filter -->
        <select
            value={filters.os}
            onchange={(e) => updateFilters({ os: e.currentTarget.value })}
            aria-label="Filter by operating system"
            class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
        >
            {#each osOptions as opt}
                <option value={opt.value}>{opt.label}</option>
            {/each}
        </select>

        <!-- Type Filter -->
        <select
            value={filters.type}
            onchange={(e) => updateFilters({ type: e.currentTarget.value })}
            aria-label="Filter by machine type"
            class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
        >
            {#each typeOptions as opt}
                <option value={opt.value}>{opt.label}</option>
            {/each}
        </select>

        <!-- Status Filter -->
        <select
            value={filters.status}
            onchange={(e) => updateFilters({ status: e.currentTarget.value })}
            aria-label="Filter by status"
            class="rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
        >
            {#each statusOptions as opt}
                <option value={opt.value}>{opt.label}</option>
            {/each}
        </select>
    </div>

    <!-- Data Table -->
    {#if machines.items.length === 0}
        {#if canAdminMachines(data.user)}
            <EmptyState
                title="No machines registered"
                description="Create a registration token to enroll your first machine."
            />
            <div class="flex justify-center -mt-4">
                <a
                    href="/machines/register"
                    class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
                >
                    Register a Machine
                </a>
            </div>
        {:else}
            <EmptyState title="No machines found" description="Try adjusting your search or filters." />
        {/if}
    {:else}
        <div
            class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
        >
            <div class="flex items-center justify-between border-b border-surface-200 px-4 py-3 dark:border-surface-700">
                <span class="text-sm font-medium text-surface-600 dark:text-surface-300">
                    Machines <span class="font-normal text-surface-400">({machines.totalCount})</span>
                </span>
            </div>
            <div class="overflow-x-auto">
                <table class="w-full text-left text-sm">
                    <thead>
                        <tr class="border-b border-surface-200 dark:border-surface-700">
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                Name
                            </th>
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                Hostname
                            </th>
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                OS
                            </th>
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                Type
                            </th>
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                Status
                            </th>
                            <th
                                scope="col"
                                class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
                            >
                                Last Ping
                            </th>
                            <th scope="col" class="w-8"></th>
                        </tr>
                    </thead>
                    <tbody class="divide-y divide-surface-100 dark:divide-surface-700">
                        {#each machines.items as machine, i (machine.id)}
                            <tr
                                class="group transition hover:bg-surface-50 dark:hover:bg-surface-700/50 {machine.isOnline ? 'border-l-2 border-l-green-500' : 'border-l-2 border-l-gray-300 dark:border-l-gray-600'} {i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''}"
                            >
                                <td class="px-6 py-4">
                                    <a
                                        href="/machines/{machine.id}"
                                        class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
                                    >
                                        {machine.name}
                                    </a>
                                </td>
                                <td
                                    class="px-6 py-4 text-surface-700 dark:text-surface-300"
                                >
                                    {machine.hostname}
                                </td>
                                <td
                                    class="px-6 py-4 text-surface-700 dark:text-surface-300"
                                >
                                    {getOsName(machine.operatingSystem)}
                                </td>
                                <td
                                    class="px-6 py-4 text-surface-700 dark:text-surface-300"
                                >
                                    {getTypeName(machine.machineType)}
                                </td>
                                <td class="px-6 py-4">
                                    {#if machine.isOnline}
                                        <span
                                            class="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400"
                                            aria-label="Status: Online"
                                        >
                                            <span class="h-2 w-2 rounded-full bg-green-500" aria-hidden="true"></span>
                                            Online
                                        </span>
                                    {:else}
                                        <span
                                            class="inline-flex items-center gap-1.5 rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-600 dark:bg-surface-700 dark:text-surface-400"
                                            aria-label="Status: Offline"
                                        >
                                            <span class="h-2 w-2 rounded-full bg-surface-400 dark:bg-surface-500" aria-hidden="true"></span>
                                            Offline
                                        </span>
                                    {/if}
                                </td>
                                <td
                                    class="px-6 py-4 text-surface-500 dark:text-surface-400"
                                >
                                    {formatRelativeTime(machine.lastPing)}
                                </td>
                                <td class="px-2 py-3"><a href="/machines/{machine.id}"><ChevronRight class="h-4 w-4 text-surface-300 opacity-0 transition-opacity group-hover:opacity-100" /></a></td>
                            </tr>
                        {/each}
                    </tbody>
                </table>
            </div>
        </div>

        <!-- Pagination -->
        <div class="flex justify-center">
            <Pagination
                page={machines.page}
                totalPages={machines.totalPages}
                onchange={handlePageChange}
            />
        </div>
    {/if}
</div>
