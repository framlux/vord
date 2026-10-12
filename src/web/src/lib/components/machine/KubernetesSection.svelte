<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { MachineKubernetesDto } from '$lib/api/types';
	import { describeAgentMessage } from '$lib/utils/clusterReporting';
	import { formatRelativeTime } from '$lib/utils/format';
	import { distroLabel } from '$lib/utils/kubernetes';

	let { kubernetes }: { kubernetes: MachineKubernetesDto | null } = $props();

	const local = $derived(kubernetes?.local ?? null);
	const node = $derived(kubernetes?.node ?? null);

	// The machine's own latest report is preferred. Once it is older than the server's window the
	// linked node still carries the name, version and role from the last time it was heard from.
	const nodeName = $derived(local?.nodeName ?? node?.nodeName ?? null);
	const kubeletVersion = $derived(
		local !== null ? local.kubeletVersion : (node?.kubeletVersion ?? '')
	);
	const controlPlane = $derived(local?.controlPlane ?? node?.controlPlane ?? null);
	const candidate = $derived(kubernetes?.candidate ?? null);
</script>

{#if kubernetes !== null}
	<div
		data-testid="kubernetes-section"
		class="space-y-3 border-t border-surface-200 pt-3 dark:border-surface-700"
	>
		<h4 class="text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">
			Kubernetes
		</h4>

		{#if local !== null}
			<div class="flex justify-between">
				<span class="text-sm text-surface-500 dark:text-surface-400">Distribution</span>
				<span data-testid="kubernetes-distro" class="text-sm text-surface-900 dark:text-surface-100">
					{distroLabel(local.distro)}
				</span>
			</div>
		{/if}

		{#if nodeName !== null}
			<div class="flex justify-between gap-4">
				<span class="text-sm text-surface-500 dark:text-surface-400">Node Name</span>
				<span
					data-testid="kubernetes-node-name"
					class="text-right font-mono text-sm text-surface-900 dark:text-surface-100"
				>
					{nodeName}
					{#if local?.nodeNameSource === 'hostname'}
						<span
							data-testid="kubernetes-node-name-note"
							class="font-sans text-xs text-surface-500 dark:text-surface-400"
						>
							(from hostname)
						</span>
					{/if}
				</span>
			</div>
		{/if}

		<div class="flex justify-between">
			<span class="text-sm text-surface-500 dark:text-surface-400">Kubelet Version</span>
			<span
				data-testid="kubernetes-kubelet-version"
				class="font-mono text-sm text-surface-900 dark:text-surface-100"
			>
				{kubeletVersion === '' ? '---' : kubeletVersion}
			</span>
		</div>

		{#if controlPlane !== null}
			<div class="flex justify-between">
				<span class="text-sm text-surface-500 dark:text-surface-400">Control Plane</span>
				<span
					data-testid="kubernetes-control-plane"
					class="text-sm text-surface-900 dark:text-surface-100"
				>
					{controlPlane ? 'Yes' : 'No'}
				</span>
			</div>
		{/if}

		<div class="flex justify-between gap-4">
			<span class="text-sm text-surface-500 dark:text-surface-400">Cluster</span>
			<span
				data-testid="kubernetes-cluster"
				class="text-right text-sm text-surface-900 dark:text-surface-100"
			>
				{#if kubernetes.grouping === 'grouped' && kubernetes.clusterId !== null}
					<a
						href="/clusters/{kubernetes.clusterId}"
						class="font-medium text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
					>
						{kubernetes.clusterName}
					</a>
				{:else if kubernetes.grouping === 'ambiguous'}
					Several clusters share this CA. The cluster is ambiguous.
				{:else if kubernetes.grouping === 'unknown'}
					Kubernetes node, cluster unknown
				{:else}
					Not grouped yet
				{/if}
			</span>
		</div>

		{#if candidate !== null}
			<div class="flex justify-between">
				<span class="text-sm text-surface-500 dark:text-surface-400">Cluster Reporter</span>
				<span
					data-testid="kubernetes-reporter"
					class="text-sm text-surface-900 dark:text-surface-100"
				>
					{candidate.isHolder ? 'Holder' : 'Candidate'}
				</span>
			</div>
			<div class="flex justify-between">
				<span class="text-sm text-surface-500 dark:text-surface-400">Last Reported</span>
				<span
					data-testid="kubernetes-reporter-last-report"
					class="text-sm text-surface-900 dark:text-surface-100"
				>
					{formatRelativeTime(candidate.lastReportAt)}
				</span>
			</div>
			{#if candidate.reporterError !== null}
				{@const reported = describeAgentMessage(candidate.reporterError)}
				<p
					data-testid="kubernetes-reporter-error"
					class="rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-xs text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-200"
				>
					{reported.label}
					{#if reported.detail !== null}
						<span class="block text-red-700 dark:text-red-300">{reported.detail}</span>
					{/if}
				</p>
			{/if}
		{/if}

		{#if node?.fingerprintMismatch}
			<p
				data-testid="kubernetes-mismatch"
				class="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
			>
				This node reports a different cluster CA than its cluster. This is expected during a CA rotation.
			</p>
		{/if}
	</div>
{/if}
