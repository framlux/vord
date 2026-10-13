<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { KubernetesClusterDetailDto } from '$lib/api/types';
	import { describeReporterError } from '$lib/utils/clusterReporting';
	import { formatDateTime } from '$lib/utils/format';
	import { TriangleAlert } from 'lucide-svelte';

	let {
		cluster,
		canAdmin,
		busy,
		onReenrol,
		onUpdatePermissions
	}: {
		cluster: KubernetesClusterDetailDto;
		canAdmin: boolean;
		busy: boolean;
		onReenrol: () => void;
		onUpdatePermissions: () => void;
	} = $props();

	interface Banner {
		id: string;
		tone: 'bad' | 'warn';
		text: string;
		action?: { label: string; run: () => void };
	}

	// The error banners come first because they are the ones that stop the cluster reporting.
	function errorBanner(code: string): Banner {
		switch (code) {
			case 'credentials_rejected':
				return {
					id: 'credentials-rejected',
					tone: 'bad',
					text: "The cluster rejected the reporter's credentials. The token was revoked or the ServiceAccount was deleted. Enrol the reporters again to issue new ones.",
					action: { label: 'Re-enrol', run: onReenrol }
				};
			case 'tls_ca_changed':
				return {
					id: 'tls-ca-changed',
					tone: 'bad',
					text: "The cluster's certificate authority changed, so the reporter no longer trusts the API server. Enrol the reporters again to pick up the new CA.",
					action: { label: 'Re-enrol', run: onReenrol }
				};
			case 'state_copied':
				return {
					id: 'state-copied',
					tone: 'bad',
					text: 'A host holds reporter state that was copied from another host, so it cannot report. Unenrol that host, or enrol it again to give it a reporter of its own.',
					action: { label: 'Re-enrol', run: onReenrol }
				};
			case 'api_unreachable':
				return {
					id: 'api-unreachable',
					tone: 'bad',
					text: "The reporter cannot reach the cluster's API server. It keeps trying, so no action is needed unless this lasts."
				};
			case 'namespace_terminating':
				return {
					id: 'namespace-terminating',
					tone: 'bad',
					text: 'The vord-system namespace is being deleted, so reporting cannot continue until the deletion finishes. Enable reporting again afterwards if it should stay on.'
				};
			default:
				return { id: 'reporter-error', tone: 'bad', text: `The reporter reports an error: ${describeReporterError(code)}.` };
		}
	}

	const banners: Banner[] = $derived.by(() => {
		const list: Banner[] = [];
		if (cluster.reporterError !== null) {
			list.push(errorBanner(cluster.reporterError));
		}
		if (cluster.reportingState === 'role_outdated') {
			list.push({
				id: 'role-outdated',
				tone: 'warn',
				text: "The reporter's permissions are older than this version of Vord needs, so some details may be missing until they are updated.",
				action: { label: 'Update permissions', run: onUpdatePermissions }
			});
		}
		if (cluster.staleSince !== null) {
			list.push({
				id: 'stale',
				tone: 'warn',
				text: `Stale since ${formatDateTime(cluster.staleSince)}. No reporter has sent a snapshot since, so node health, versions and pod counts date from then.`
			});
		}
		if (cluster.truncated) {
			list.push({
				id: 'truncated',
				tone: 'warn',
				text: "Only some of this cluster's nodes are listed, so the node table is incomplete."
			});
		}
		if (cluster.hasFingerprintMismatch) {
			list.push({
				id: 'fingerprint-mismatch',
				tone: 'warn',
				text: 'Some nodes report a different cluster CA than this cluster. This is expected during a CA rotation.'
			});
		}

		return list;
	});

	const toneClasses = {
		bad: 'border-red-300 bg-red-50 text-red-800 dark:border-red-700 dark:bg-red-900/20 dark:text-red-200',
		warn: 'border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300'
	};
</script>

{#each banners as banner (banner.id)}
	<div
		data-testid="{banner.id}-banner"
		role="alert"
		class="flex flex-wrap items-start gap-3 rounded-lg border px-4 py-3 text-sm {toneClasses[banner.tone]}"
	>
		<TriangleAlert size={16} class="mt-0.5 shrink-0" />
		<p class="min-w-0 flex-1">{banner.text}</p>
		{#if banner.action !== undefined && canAdmin}
			<button
				type="button"
				onclick={banner.action.run}
				disabled={busy}
				class="rounded-lg border border-current px-3 py-1 text-xs font-medium transition hover:bg-black/5 disabled:opacity-50 dark:hover:bg-white/10"
			>
				{banner.action.label}
			</button>
		{/if}
	</div>
{/each}
