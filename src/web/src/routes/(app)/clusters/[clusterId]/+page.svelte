<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { KubernetesClusterDetailDto } from '$lib/api/types';
	import { formatRelativeTime } from '$lib/utils/format';
	import { canAdminMachines } from '$lib/utils/roles';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import { ArrowLeft, TriangleAlert } from 'lucide-svelte';

	let { data, form } = $props();

	const cluster: KubernetesClusterDetailDto = $derived(data.cluster);

	// After a rejected rename the box keeps what was typed, so a name that was too long can be
	// trimmed rather than retyped.
	const nameInputValue = $derived(form?.displayName ?? cluster.displayName ?? '');

	const headerClass =
		'px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400';
</script>

<svelte:head><title>{cluster.name} - Vord</title></svelte:head>

<div class="space-y-6">
	<div class="space-y-3">
		<a
			href="/clusters"
			class="inline-flex items-center gap-1.5 text-sm text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200"
		>
			<ArrowLeft size={14} />
			Clusters
		</a>
		<PageHeader title={cluster.name} />
	</div>

	{#if cluster.hasFingerprintMismatch}
		<div
			data-testid="fingerprint-mismatch-banner"
			class="flex items-start gap-3 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
		>
			<TriangleAlert size={16} class="mt-0.5 shrink-0" />
			<p>
				Some nodes report a different cluster CA than this cluster. This is expected during a CA rotation.
			</p>
		</div>
	{/if}

	{#if canAdminMachines(data.user)}
		<form
			method="POST"
			action="?/rename"
			class="space-y-2 rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800"
		>
			<label for="cluster-display-name" class="block text-sm font-medium text-surface-700 dark:text-surface-300">
				Cluster name
			</label>
			<div class="flex flex-wrap items-center gap-3">
				<input
					id="cluster-display-name"
					name="displayName"
					type="text"
					maxlength="100"
					placeholder="Default name"
					value={nameInputValue}
					class="min-w-[240px] flex-1 rounded-lg border border-surface-200 bg-surface-50 px-3 py-2 text-sm text-surface-900 placeholder-surface-400 transition focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100 dark:placeholder-surface-500"
				/>
				<button
					type="submit"
					class="rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
				>
					Rename
				</button>
			</div>
			<p class="text-xs text-surface-500 dark:text-surface-400">Leave empty to use the default name.</p>
			{#if form?.message}
				<div
					role="alert"
					class="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-200"
				>
					{form.message}
				</div>
			{/if}
		</form>
	{/if}

	<div
		class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="overflow-x-auto">
			<table class="w-full text-left text-sm">
				<thead>
					<tr class="border-b border-surface-200 dark:border-surface-700">
						<th scope="col" class={headerClass}>Node</th>
						<th scope="col" class={headerClass}>Role</th>
						<th scope="col" class={headerClass}>Kubelet</th>
						<th scope="col" class={headerClass}>Machine</th>
						<th scope="col" class={headerClass}>Cluster CA</th>
						<th scope="col" class={headerClass}>Last report</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
					{#each cluster.nodes as node (node.id)}
						<tr class="transition hover:bg-surface-100/60 dark:hover:bg-surface-700/50">
							<td class="px-6 py-4 font-medium text-surface-900 dark:text-surface-100">
								{node.nodeName}
							</td>
							<td class="px-6 py-4">
								{#if node.controlPlane}
									<span
										class="inline-flex items-center rounded-full bg-primary-500/10 px-2.5 py-0.5 text-xs font-medium text-primary-600 dark:text-primary-400"
									>
										control plane
									</span>
								{/if}
							</td>
							<td class="px-6 py-4 font-mono text-xs text-surface-700 dark:text-surface-300">
								{node.kubeletVersion ?? '---'}
							</td>
							<td class="px-6 py-4">
								{#if node.machineId !== null}
									<a
										href="/machines/{node.machineId}"
										class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
									>
										{node.machineName}
									</a>
								{:else}
									<span class="text-surface-500 dark:text-surface-400">Not managed by Vord</span>
								{/if}
							</td>
							<td class="px-6 py-4">
								{#if node.fingerprintMismatch}
									<span
										class="inline-flex items-center rounded-full bg-amber-100 px-2.5 py-0.5 text-xs font-medium text-amber-800 dark:bg-amber-900/30 dark:text-amber-300"
									>
										CA mismatch
									</span>
								{/if}
							</td>
							<td class="px-6 py-4 text-surface-500 dark:text-surface-400">
								{node.lastLocalReportAt === null ? '---' : formatRelativeTime(node.lastLocalReportAt)}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
</div>
