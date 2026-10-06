<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { goto } from '$app/navigation';
	import { ApiClient, ApiError } from '$lib/api/client';

	let { machineId, machineName, csrfToken }: { machineId: number; machineName: string; csrfToken: string | undefined } = $props();

	const uninstallGuide = 'https://vordfleet.dev/support/fleet-management/agent-troubleshooting';

	let confirming = $state(false);
	let removing = $state(false);
	let error = $state('');
	let alreadyGone = $state(false);

	async function remove() {
		// The confirmation button stays on screen while the request is in flight, so a second click
		// must not send a second delete.
		if (removing) {
			return;
		}

		removing = true;
		error = '';
		try {
			const api = new ApiClient('', fetch, csrfToken);
			await api.deleteMachine(machineId);
			// Reload the layout data too, so the subscription's machine count (and the over-limit
			// notice built from it) reflects the removal as soon as the list opens.
			await goto('/machines', { invalidateAll: true });
		} catch (err: unknown) {
			if (err instanceof ApiError && err.status === 404) {
				alreadyGone = true;
				error = 'This host has already been removed.';
			} else {
				error = err instanceof Error ? err.message : 'Failed to remove the host.';
			}
			removing = false;
		}
	}
</script>

<div class="mt-4 rounded-xl border border-red-200 bg-red-50/40 p-4 sm:p-5 dark:border-red-900/50 dark:bg-red-900/10">
	<h3 class="text-xs font-semibold uppercase tracking-wider text-red-700 dark:text-red-400">Remove host</h3>
	{#if confirming}
		<p class="mt-2 text-sm text-surface-700 dark:text-surface-300">
			Uninstall the agent from {machineName} first. A host removed while its agent is still running keeps
			trying to report with a key that no longer works.
			<a href={uninstallGuide} target="_blank" rel="external noopener noreferrer" class="font-medium underline">How to uninstall the agent</a>
		</p>
		<p class="mt-2 text-sm text-surface-700 dark:text-surface-300">
			Removing a host deletes it from your fleet. Its history is no longer shown.
		</p>
		<div class="mt-3 flex items-center gap-2">
			<button
				onclick={remove}
				disabled={removing}
				class="rounded bg-red-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-red-700 disabled:opacity-60"
			>
				Yes, remove
			</button>
			<button
				onclick={() => { confirming = false; error = ''; }}
				disabled={removing}
				class="rounded border border-surface-300 px-3 py-1.5 text-xs font-medium text-surface-700 hover:bg-surface-50 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
			>
				No
			</button>
		</div>
	{:else}
		<p class="mt-2 text-sm text-surface-600 dark:text-surface-400">
			Remove this host from your fleet, for example when it has been decommissioned.
		</p>
		<button
			onclick={() => (confirming = true)}
			class="mt-3 rounded-md border border-red-300 px-3 py-1.5 text-xs font-medium text-red-700 hover:bg-red-100 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-900/30"
		>
			Remove host
		</button>
	{/if}
	{#if error}
		<p role="alert" class="mt-3 text-sm text-red-700 dark:text-red-400">
			{error}
			{#if alreadyGone}
				<a href="/machines" class="ml-1 font-medium underline">Back to machines</a>
			{/if}
		</p>
	{/if}
</div>
