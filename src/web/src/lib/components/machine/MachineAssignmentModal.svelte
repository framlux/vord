<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { untrack } from 'svelte';
	import { ApiClient } from '$lib/api/client';
	import { MachineHealthStatus, OperatingSystem, MachineType, type FleetMachineDto } from '$lib/api/types';
	import HealthBadge from '$lib/components/HealthBadge.svelte';
	import Pagination from '$lib/components/Pagination.svelte';

	let {
		open = false,
		title,
		initialSelectedIds = [],
		allowEmpty = false,
		onsave,
		oncancel
	}: {
		open?: boolean;
		title: string;
		initialSelectedIds?: number[];
		allowEmpty?: boolean;
		onsave?: (machineIds: number[], offeredMachineIds: number[]) => void;
		oncancel?: () => void;
	} = $props();

	// The endpoint refuses a page larger than this outright rather than clamping, so asking for
	// more would render nothing at all rather than a short page.
	const PAGE_SIZE = 25;
	const SEARCH_DEBOUNCE_MS = 250;

	const client = new ApiClient('', fetch);

	// The selection is a set of ids owned by this component, never derived from the rendered page.
	// The list is a window onto the fleet; binding the selection to it would silently drop every
	// machine that is off-screen at save time.
	let selected = $state<Set<number>>(new Set());

	// What the caller was able to choose from. The API may only unassign ids named here, so this
	// has to include every machine this modal represents — the ones it drew, the ones a bulk
	// selection resolved, and the ones it arrived holding, which the user can untick on sight.
	let offered = $state<Set<number>>(new Set());

	let machines = $state<FleetMachineDto[]>([]);
	let page = $state(1);
	let totalPages = $state(1);
	let loading = $state(false);
	let loadError = $state<string | null>(null);
	let capNotice = $state<string | null>(null);

	let search = $state('');
	let healthStatus = $state('');
	let os = $state('');
	let machineType = $state('');

	let dialogElement = $state<HTMLDivElement | undefined>(undefined);
	let previouslyFocused: HTMLElement | null = null;
	let searchTimer: ReturnType<typeof setTimeout> | undefined;

	const selectedCount = $derived(selected.size);
	const visibleIds = $derived(new Set(machines.map((m) => m.id)));
	const offPageCount = $derived([...selected].filter((id) => visibleIds.has(id) === false).length);
	const saveBlocked = $derived(allowEmpty === false && selectedCount === 0);

	// Only the filters this picker exposes travel to the server, and the same four reach both the
	// list and the bulk-selection call, so "select all matching" can never resolve a different set
	// than the one on screen.
	function currentFilters(): { search?: string; healthStatus?: string; os?: string; type?: string } {
		return {
			search: search.trim().length > 0 ? search.trim() : undefined,
			healthStatus: healthStatus.length > 0 ? healthStatus : undefined,
			os: os.length > 0 ? os : undefined,
			type: machineType.length > 0 ? machineType : undefined
		};
	}

	async function load(requestedPage: number) {
		loading = true;
		loadError = null;
		try {
			const result = await client.searchMachines({
				page: requestedPage,
				pageSize: PAGE_SIZE,
				...currentFilters()
			});

			machines = result.items;
			page = result.page;
			totalPages = result.totalPages;
			offered = new Set([...offered, ...result.items.map((m) => m.id)]);
		} catch {
			// An empty list and a failed request look identical on screen, and one of them means
			// "this fleet has no machines" while the other means "we do not know".
			machines = [];
			loadError = 'Could not load machines. Try again.';
		} finally {
			loading = false;
		}
	}

	function applyFilterChange() {
		clearTimeout(searchTimer);
		searchTimer = setTimeout(() => load(1), SEARCH_DEBOUNCE_MS);
	}

	function toggle(id: number) {
		const next = new Set(selected);
		if (next.has(id)) {
			next.delete(id);
		} else {
			next.add(id);
		}
		selected = next;
	}

	async function selectAllMatching() {
		capNotice = null;
		try {
			const selection = await client.getMachineIds(currentFilters());
			selected = new Set([...selected, ...selection.ids]);
			offered = new Set([...offered, ...selection.ids]);

			if (selection.truncated) {
				// The ids endpoint hands back a prefix of the match and says so. Presenting that as
				// the whole filter would be the same silent omission this picker exists to end.
				capNotice = `Too many machines match this filter (${selection.totalCount.toLocaleString()}). The first ${selection.ids.length.toLocaleString()} were selected — narrow the filter to reach the rest.`;
			}
		} catch {
			loadError = 'Could not select all matching machines. Try again.';
		}
	}

	function clearSelection() {
		selected = new Set();
		capNotice = null;
	}

	function save() {
		if (saveBlocked) {
			return;
		}

		onsave?.([...selected], [...offered]);
	}

	function trapFocus(event: KeyboardEvent) {
		if (event.key !== 'Tab') {
			return;
		}

		// Every control in this dialog, not just its buttons: a trap that skips the search box,
		// the filters and the checkboxes lets focus escape the moment someone tabs through the list.
		const focusable = dialogElement?.querySelectorAll<HTMLElement>(
			'button:not([disabled]), input:not([disabled]), select:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'
		);
		if (focusable === undefined || focusable.length === 0) {
			return;
		}

		const first = focusable[0];
		const last = focusable[focusable.length - 1];

		if (event.shiftKey && document.activeElement === first) {
			event.preventDefault();
			last.focus();
		} else if (event.shiftKey === false && document.activeElement === last) {
			event.preventDefault();
			first.focus();
		}
	}

	// Opening and closing is the only thing that may reset this dialog, so the body runs untracked.
	// Left tracked, it reaches the filter state through load() — which reads the filters before its
	// first await — and every keystroke in the search box would re-run this and re-seed the
	// selection from the props, discarding whatever the user had ticked. Surviving a filter change
	// is the whole point of holding the selection here rather than on the rendered rows.
	$effect(() => {
		const isOpen = open;

		untrack(() => {
			if (isOpen) {
				previouslyFocused = document.activeElement as HTMLElement;
				selected = new Set(initialSelectedIds);
				offered = new Set(initialSelectedIds);
				page = 1;
				capNotice = null;
				load(1);

				requestAnimationFrame(() => {
					const target = dialogElement?.querySelector<HTMLElement>('input, button');
					target?.focus();
				});
			} else if (previouslyFocused !== null) {
				previouslyFocused.focus();
				previouslyFocused = null;
			}
		});
	});

	const healthOptions = [
		{ value: '', label: 'Any health' },
		{ value: 'healthy', label: 'Healthy' },
		{ value: 'warning', label: 'Warning' },
		{ value: 'critical', label: 'Critical' },
		{ value: 'offline', label: 'Offline' }
	];

	// The server parses these by enum name, case-insensitively, so the option values are the names
	// themselves rather than the numeric values the DTOs carry.
	const osOptions = [OperatingSystem.Ubuntu, OperatingSystem.Debian, OperatingSystem.RedHat, OperatingSystem.Fedora, OperatingSystem.Windows, OperatingSystem.MacOS];
	const typeOptions = [MachineType.BareMetalServer, MachineType.VirtualMachine, MachineType.Desktop, MachineType.Laptop];
</script>

<svelte:window onkeydown={(e) => { if (open && e.key === 'Escape') oncancel?.(); }} />

{#if open}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" role="presentation">
		<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
		<div
			bind:this={dialogElement}
			onkeydown={trapFocus}
			tabindex="-1"
			role="dialog"
			aria-modal="true"
			aria-labelledby="machine-assignment-title"
			class="flex max-h-[85vh] w-full max-w-2xl flex-col rounded-xl border border-surface-200 bg-white shadow-xl dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="border-b border-surface-200 p-4 dark:border-surface-700">
				<h2 id="machine-assignment-title" class="text-lg font-semibold text-surface-900 dark:text-surface-50">
					{title}
				</h2>
			</div>

			<div class="grid grid-cols-1 gap-2 border-b border-surface-200 p-4 sm:grid-cols-2 dark:border-surface-700">
				<div class="sm:col-span-2">
					<label for="machine-picker-search" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Search machines</label>
					<input
						id="machine-picker-search"
						type="search"
						bind:value={search}
						oninput={applyFilterChange}
						placeholder="Name, hostname or model"
						class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
					/>
				</div>
				<div>
					<label for="machine-picker-health" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Health</label>
					<select id="machine-picker-health" bind:value={healthStatus} onchange={applyFilterChange} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
						{#each healthOptions as option}
							<option value={option.value}>{option.label}</option>
						{/each}
					</select>
				</div>
				<div class="grid grid-cols-2 gap-2">
					<div>
						<label for="machine-picker-os" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">OS</label>
						<select id="machine-picker-os" bind:value={os} onchange={applyFilterChange} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
							<option value="">Any OS</option>
							{#each osOptions as option}
								<option value={OperatingSystem[option]}>{OperatingSystem[option]}</option>
							{/each}
						</select>
					</div>
					<div>
						<label for="machine-picker-type" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Type</label>
						<select id="machine-picker-type" bind:value={machineType} onchange={applyFilterChange} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
							<option value="">Any type</option>
							{#each typeOptions as option}
								<option value={MachineType[option]}>{MachineType[option]}</option>
							{/each}
						</select>
					</div>
				</div>
			</div>

			<div class="flex flex-wrap items-center justify-between gap-2 border-b border-surface-200 px-4 py-2 text-xs dark:border-surface-700">
				<div class="flex items-center gap-3">
					<span class="font-medium text-surface-700 dark:text-surface-300">{selectedCount} selected</span>
					{#if offPageCount > 0}
						<span class="text-surface-500 dark:text-surface-400">
							{offPageCount}
							{offPageCount === 1 ? 'machine you selected is not shown on this page' : 'machines you selected are not shown on this page'}
						</span>
					{/if}
				</div>
				<div class="flex items-center gap-2">
					<button type="button" onclick={selectAllMatching} class="rounded border border-surface-300 px-2 py-1 font-medium text-primary-600 hover:bg-surface-100 dark:border-surface-600 dark:text-primary-400 dark:hover:bg-surface-700">
						Select all matching
					</button>
					<button type="button" onclick={clearSelection} class="rounded border border-surface-300 px-2 py-1 font-medium text-surface-600 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700">
						Clear selection
					</button>
				</div>
			</div>

			{#if capNotice}
				<p class="border-b border-amber-200 bg-amber-50 px-4 py-2 text-xs text-amber-700 dark:border-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
					{capNotice}
				</p>
			{/if}

			<div class="min-h-0 flex-1 overflow-y-auto p-4">
				{#if loadError}
					<p role="alert" class="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
						{loadError}
					</p>
				{:else if loading && machines.length === 0}
					<p class="py-6 text-center text-sm text-surface-500 dark:text-surface-400">Loading machines…</p>
				{:else if machines.length === 0}
					<p class="py-6 text-center text-sm text-surface-500 dark:text-surface-400">No machines match this filter.</p>
				{:else}
					<ul class="space-y-1">
						{#each machines as machine (machine.id)}
							<li>
								<label class="flex items-center gap-3 rounded px-2 py-1.5 hover:bg-surface-100 dark:hover:bg-surface-700">
									<input
										type="checkbox"
										class="checkbox"
										aria-label="Select {machine.name}"
										checked={selected.has(machine.id)}
										onchange={() => toggle(machine.id)}
									/>
									<span class="flex-1 text-sm text-surface-800 dark:text-surface-200">{machine.name}</span>
									{#if machine.hostname}
										<span class="hidden text-xs text-surface-400 sm:inline dark:text-surface-500">{machine.hostname}</span>
									{/if}
									<HealthBadge status={machine.healthStatus as MachineHealthStatus} />
								</label>
							</li>
						{/each}
					</ul>
				{/if}
			</div>

			<div class="flex items-center justify-between gap-2 border-t border-surface-200 p-4 dark:border-surface-700">
				<Pagination {page} {totalPages} onchange={(p) => load(p)} />
				<div class="flex items-center gap-3">
					{#if saveBlocked}
						<span class="text-xs text-surface-500 dark:text-surface-400">Select at least one machine.</span>
					{/if}
					<button type="button" onclick={() => oncancel?.()} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">
						Cancel
					</button>
					<button type="button" onclick={save} disabled={saveBlocked} class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 disabled:cursor-not-allowed disabled:opacity-50 dark:bg-primary-500 dark:hover:bg-primary-600">
						Save
					</button>
				</div>
			</div>
		</div>
	</div>
{/if}
