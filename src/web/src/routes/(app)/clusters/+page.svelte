<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { KubernetesClusterSummaryDto } from '$lib/api/types';
	import { formatRelativeTime } from '$lib/utils/format';
	import { distroLabel, reportingBadge, type BadgeTone } from '$lib/utils/kubernetes';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';

	let { data } = $props();

	const clusters: KubernetesClusterSummaryDto[] = $derived(data.clusters);

	const toneClasses: Record<BadgeTone, string> = {
		ok: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
		bad: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
		warn: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300',
		neutral: 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300'
	};

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
							{@const badge = reportingBadge(cluster.reportingState, cluster.activeReporterMachineName)}
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
										data-testid="reporting-badge"
										class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {toneClasses[badge.tone]}"
									>
										{badge.label}
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
