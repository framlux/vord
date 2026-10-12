<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { KubernetesClusterSummaryDto } from '$lib/api/types';
	import { formatRelativeTime } from '$lib/utils/format';
	import { distroLabel } from '$lib/utils/kubernetes';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';

	let { data } = $props();

	const clusters: KubernetesClusterSummaryDto[] = $derived(data.clusters);

	// Every cluster is built only from what its machines report about themselves, so the one
	// state there is today reads "Identity only". Any state this page has no label for is shown
	// as the server sent it rather than hidden.
	function reportingLabel(state: string): string {
		return state === 'identity_only' ? 'Identity only' : state;
	}

	const headerClass =
		'px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400';
</script>

<svelte:head><title>Clusters - Vord</title></svelte:head>

<div class="space-y-6">
	<PageHeader title="Clusters" />

	{#if clusters.length === 0}
		<EmptyState
			title="No Kubernetes clusters yet"
			description="Clusters appear automatically when a Vord agent runs on a Kubernetes node."
		/>
	{:else}
		<div
			class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th scope="col" class={headerClass}>Name</th>
							<th scope="col" class={headerClass}>Distro</th>
							<th scope="col" class={headerClass}>Nodes</th>
							<th scope="col" class={headerClass}>Managed</th>
							<th scope="col" class={headerClass}>Unmanaged</th>
							<th scope="col" class={headerClass}>Kubelet</th>
							<th scope="col" class={headerClass}>Reporting</th>
							<th scope="col" class={headerClass}>Last seen</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each clusters as cluster (cluster.id)}
							<tr class="transition hover:bg-surface-100/60 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4">
									<a
										href="/clusters/{cluster.id}"
										class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
									>
										{cluster.name}
									</a>
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{distroLabel(cluster.distro)}
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">{cluster.nodeCount}</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{cluster.managedNodeCount}
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{cluster.unmanagedNodeCount}
								</td>
								<td class="px-6 py-4 font-mono text-xs text-surface-700 dark:text-surface-300">
									{cluster.kubeletVersions.length > 0 ? cluster.kubeletVersions.join(', ') : '---'}
								</td>
								<td class="px-6 py-4">
									<span
										class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-600 dark:bg-surface-700 dark:text-surface-300"
									>
										{reportingLabel(cluster.reportingState)}
									</span>
								</td>
								<td class="px-6 py-4 text-surface-500 dark:text-surface-400">
									{formatRelativeTime(cluster.lastSeenAt)}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}
</div>
