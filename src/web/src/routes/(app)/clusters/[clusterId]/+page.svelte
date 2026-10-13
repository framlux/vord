<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { onMount } from 'svelte';
	import { invalidateAll } from '$app/navigation';
	import { ApiClient } from '$lib/api/client';
	import type {
		KubernetesCandidateDto,
		KubernetesClusterDetailDto,
		KubernetesEligibleMachineDto,
		SigningKeyDto
	} from '$lib/api/types';
	import { getLocalKeys } from '$lib/crypto/signing';
	import { sendSignedCommand, usableSigningKeys, type LocalSigningKey } from '$lib/commands/signedCommand';
	import {
		AGENT_ENROL,
		AGENT_UNENROL,
		describeBlockReason,
		describeReporterError,
		describeSkip,
		disableBlock,
		planDisable,
		planEnable,
		planEnrolOne,
		planReenrol,
		planUnenrolOne,
		planUpdatePermissions,
		type CommandResult,
		type ReporterCommandType,
		type ReportingPlan,
		sendBlock,
		sendPlanned,
		type SkipReason
	} from '$lib/utils/clusterReporting';
	import { formatDateTime, formatRelativeTime } from '$lib/utils/format';
	import {
		conditionLabel,
		nodeRoles,
		podsLabel,
		readyBadge,
		reportingBadge,
		type BadgeTone
	} from '$lib/utils/kubernetes';
	import { canAdminMachines } from '$lib/utils/roles';
	import ClusterBanners from '$lib/components/cluster/ClusterBanners.svelte';
	import CommandResults from '$lib/components/cluster/CommandResults.svelte';
	import ReportingDialog from '$lib/components/cluster/ReportingDialog.svelte';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import { ArrowLeft, TriangleAlert } from 'lucide-svelte';

	let { data, form } = $props();

	const cluster: KubernetesClusterDetailDto = $derived(data.cluster);
	const canAdmin = $derived(canAdminMachines(data.user));

	// After a rejected rename the box keeps what was typed, so a name that was too long can be
	// trimmed rather than retyped.
	const nameInputValue = $derived(form?.displayName ?? cluster.displayName ?? '');

	const enableText =
		"This creates a read-only ServiceAccount in your cluster, using each control-plane node's admin credentials. " +
		'Each enrolled node stores a long-lived token that can read node details, the namespace list, and full pod ' +
		'specifications, including environment-variable values. It cannot read Secrets or ConfigMaps. Anyone who ' +
		'obtains the token, for example from a backup of /var/lib/vord-agent, gets the same access. Disabling ' +
		'reporting revokes every token.';
	const requirementsText =
		'Enrolling a host needs remote commands enabled on that host (allow_remote_commands in the agent config). ' +
		'Turning reporting off works on any host.';
	const disableText = [
		"Disabling reporting removes the reporter's ServiceAccount, token and other objects from your cluster, which " +
			'revokes every token. Nodes that only the reporter listed leave the node table, and node health and pod ' +
			'counts stop updating.',
		'One online control-plane machine uninstalls the reporter, and every other online reporter is unenrolled, ' +
			'which removes the token it stores. A host that is offline is not sent a command: unenrol it from the ' +
			'reporters table once it returns.'
	];
	const noKeyNotice =
		'No signing key from this browser is registered with the server, so commands cannot be signed here.';

	// The keys are read once, when something first needs them, so a page nobody acts on costs neither
	// an IndexedDB read nor a request.
	let localKeys = $state<LocalSigningKey[]>([]);
	let serverKeys = $state<SigningKeyDto[]>([]);
	let keysLoaded = $state(false);
	let keysLoading: Promise<void> | null = null;

	const usableKeys = $derived(usableSigningKeys(localKeys, serverKeys));
	const noKeys = $derived(keysLoaded && usableKeys.length === 0);

	async function loadKeys(): Promise<void> {
		const userId = data.user?.id;
		const tenantId = data.user?.activeTenantId;
		try {
			localKeys = userId && tenantId ? await getLocalKeys(userId, tenantId) : [];
		} catch {
			localKeys = [];
		}

		try {
			const resp = await new ApiClient('', fetch, data.user?.csrfToken ?? undefined).getSigningKeys();
			serverKeys = resp.keys.filter((k) => k.revokedAt === null);
		} catch {
			serverKeys = [];
		}

		keysLoaded = true;
	}

	function ensureKeys(): Promise<void> {
		keysLoading ??= loadKeys();

		return keysLoading;
	}

	onMount(() => {
		if (canAdmin && (cluster.eligibleMachines.length > 0 || cluster.candidates.length > 0)) {
			void ensureKeys();
		}
	});

	interface DialogState {
		kind: 'enable' | 'disable';
		plan: ReportingPlan;
		phase: 'confirm' | 'sending' | 'done';
		results: CommandResult[];
	}

	let dialog = $state<DialogState | null>(null);
	let sendingOne = $state(false);
	let panel = $state<{ notice: string | null; results: CommandResult[] } | null>(null);

	const busy = $derived(sendingOne || dialog?.phase === 'sending');
	const reportingStatus = $derived(reportingBadge(cluster.reportingState, cluster.activeReporterMachineName));
	const disableReason = $derived(disableBlock(cluster, keysLoaded ? usableKeys : null));
	const eligibleToEnrol = $derived(
		cluster.eligibleMachines.filter(
			(m) => m.enrolled === false && cluster.candidates.some((c) => c.machineId === m.machineId) === false
		)
	);
	const dialogBlocked = $derived.by(() => {
		if (dialog === null || usableKeys.length === 0 || dialog.plan.commands.length > 0) {
			return null;
		}

		return dialog.kind === 'enable'
			? 'No machine can be sent an enrol command right now.'
			: 'No eligible machine can be sent the uninstall command right now.';
	});

	// A machine that cannot be sent the row's command, as the row should explain it. The key is only
	// blamed once the keys have loaded, and only when there are keys to blame: with none, the notice
	// above the tables says so once instead of every row saying it. A pause and a disabled capability
	// are facts about the machine, so they show without waiting for the keys. A disabled capability
	// only blocks an enrol; a candidate can always be unenrolled.
	function rowBlock(machine: KubernetesEligibleMachineDto): SkipReason | null {
		return sendBlock(machine, keysLoaded ? usableKeys : null, AGENT_ENROL);
	}

	function candidateBlock(candidate: KubernetesCandidateDto): SkipReason | null {
		const described = cluster.eligibleMachines.find((m) => m.machineId === candidate.machineId);

		return described === undefined ? null : sendBlock(described, keysLoaded ? usableKeys : null, AGENT_UNENROL);
	}

	async function execute(plan: ReportingPlan): Promise<CommandResult[]> {
		const api = new ApiClient('', fetch, data.user?.csrfToken ?? undefined);

		return sendPlanned(plan.commands, async (planned) => {
			await sendSignedCommand(api, {
				machineId: planned.machineId,
				commandType: planned.commandType,
				localKeyId: planned.localKeyId,
				localKeys,
				serverKeys,
				userId: data.user?.id ?? 0,
				tenantId: data.user?.activeTenantId ?? 0
			});
		});
	}

	async function refresh(): Promise<void> {
		try {
			await invalidateAll();
		} catch {
			// The results are already on screen; the next visit loads the page again.
		}
	}

	async function openDialog(kind: DialogState['kind']) {
		if (busy) {
			return;
		}

		await ensureKeys();
		const plan = kind === 'enable' ? planEnable(cluster, usableKeys) : planDisable(cluster, usableKeys);
		dialog = { kind, plan, phase: 'confirm', results: [] };
	}

	async function confirmDialog() {
		const current = dialog;
		if (current === null || current.phase !== 'confirm') {
			return;
		}

		current.phase = 'sending';
		current.results = await execute(current.plan);
		current.phase = 'done';
		await refresh();
	}

	async function runPlan(makePlan: () => ReportingPlan, commandType: ReporterCommandType) {
		if (busy) {
			return;
		}

		sendingOne = true;
		try {
			await ensureKeys();
			if (usableKeys.length === 0) {
				panel = { notice: noKeyNotice, results: [] };

				return;
			}

			const plan = makePlan();
			const sent = await execute(plan);
			const notSent = plan.skipped.map((skipped) => ({
				machineId: skipped.machineId,
				machineName: skipped.machineName,
				commandType,
				accepted: false,
				message: describeSkip(skipped.reason)
			}));
			panel = { notice: null, results: [...sent, ...notSent] };
			await refresh();
		} finally {
			sendingOne = false;
		}
	}

	const enrolOne = (machine: KubernetesEligibleMachineDto) =>
		runPlan(() => planEnrolOne(machine, usableKeys), AGENT_ENROL);
	const unenrolOne = (candidate: KubernetesCandidateDto) =>
		runPlan(() => planUnenrolOne(cluster, candidate, usableKeys), AGENT_UNENROL);
	const reenrol = () => runPlan(() => planReenrol(cluster, usableKeys), AGENT_ENROL);
	const updatePermissions = () => runPlan(() => planUpdatePermissions(cluster, usableKeys), AGENT_ENROL);

	const toneClasses: Record<BadgeTone, string> = {
		ok: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
		bad: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
		warn: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300',
		neutral: 'bg-surface-100 text-surface-600 dark:bg-surface-700 dark:text-surface-300'
	};
	const chipClass =
		'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium';
	const headerClass =
		'px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400';
	const cardClass =
		'overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800';
	const rowButtonClass =
		'rounded-lg border border-surface-300 px-3 py-1 text-xs font-medium text-surface-700 transition hover:bg-surface-100 disabled:opacity-50 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700';
	const primaryButtonClass =
		'rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50';
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

	<div data-testid="reporting-status" class="space-y-3 rounded-xl {cardClass} p-4">
		<div class="flex flex-wrap items-center gap-x-4 gap-y-2">
			<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
				Cluster reporting
			</h2>
			<span data-testid="reporting-badge" class="{chipClass} {toneClasses[reportingStatus.tone]}">
				{reportingStatus.label}
			</span>
			{#if cluster.lastSnapshotReceivedAt !== null}
				<span class="text-sm text-surface-500 dark:text-surface-400">
					Last snapshot {formatRelativeTime(cluster.lastSnapshotReceivedAt)}
				</span>
			{/if}
			{#if cluster.clusterVersion !== null}
				<span class="font-mono text-xs text-surface-600 dark:text-surface-300">
					Kubernetes {cluster.clusterVersion}
				</span>
			{/if}
		</div>

		{#if cluster.reportingState === 'off'}
			<p class="text-sm text-surface-600 dark:text-surface-400">
				Vord knows this cluster only from what its hosts report about themselves.
				{#if cluster.eligibleMachines.length > 0}
					Enable cluster reporting to add node health, pod counts and cordon state.
				{:else}
					Cluster reporting needs a control-plane machine that holds an admin credential.
				{/if}
			</p>
		{/if}

		{#if canAdmin && cluster.reportingState === 'off' && cluster.eligibleMachines.length > 0}
			<button type="button" onclick={() => openDialog('enable')} disabled={busy} class={primaryButtonClass}>
				Enable cluster reporting
			</button>
		{:else if canAdmin && cluster.reportingState !== 'off'}
			<div class="space-y-2">
				<button
					type="button"
					onclick={() => openDialog('disable')}
					disabled={busy || disableReason !== null}
					class="rounded-lg border border-red-300 px-4 py-2 text-sm font-medium text-red-700 transition hover:bg-red-50 disabled:opacity-50 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-900/20"
				>
					Disable reporting
				</button>
				{#if disableReason !== null}
					<div class="space-y-1 text-xs text-surface-500 dark:text-surface-400">
						<p>{disableReason.summary}</p>
						{#if disableReason.machines.length > 0}
							<ul data-testid="disable-blocked-machines" class="list-disc pl-5">
								{#each disableReason.machines as blocked (blocked.machineId)}
									<li>{blocked.machineName}: {describeBlockReason(blocked.reason)}</li>
								{/each}
							</ul>
						{/if}
					</div>
				{/if}
			</div>
		{/if}

		{#if canAdmin && noKeys && (cluster.eligibleMachines.length > 0 || cluster.candidates.length > 0)}
			<p
				data-testid="no-signing-key"
				role="status"
				class="rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
			>
				{noKeyNotice}
				<a href="/settings/signing-keys" class="underline">Generate a signing key</a> first.
			</p>
		{/if}
	</div>

	<ClusterBanners
		{cluster}
		{canAdmin}
		busy={busy || noKeys}
		onReenrol={reenrol}
		onUpdatePermissions={updatePermissions}
	/>

	{#if panel !== null}
		<div class="{cardClass} p-4" role="status">
			<div class="flex items-center justify-between gap-3">
				<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
					Command results
				</h2>
				<button type="button" onclick={() => (panel = null)} class={rowButtonClass}>Dismiss</button>
			</div>
			{#if panel.notice !== null}
				<p class="mt-2 text-sm text-amber-800 dark:text-amber-300">{panel.notice}</p>
			{/if}
			<CommandResults results={panel.results} />
		</div>
	{/if}

	{#if canAdmin}
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

	{#if cluster.nodes.length === 0}
		<EmptyState
			title="No nodes reported yet"
			description="Nodes appear here once a Vord agent on one of them has reported."
		/>
	{:else}
		<div class={cardClass}>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th scope="col" class={headerClass}>Node</th>
							<th scope="col" class={headerClass}>Roles</th>
							<th scope="col" class={headerClass}>Status</th>
							<th scope="col" class={headerClass}>Kubelet</th>
							<th scope="col" class={headerClass}>Pods</th>
							<th scope="col" class={headerClass}>Machine</th>
							<th scope="col" class={headerClass}>Cluster CA</th>
							<th scope="col" class={headerClass}>Last report</th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each cluster.nodes as node (node.id)}
							{@const ready = readyBadge(node.readyStatus)}
							<tr class="transition hover:bg-surface-100/60 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4 font-medium text-surface-900 dark:text-surface-100">
									{node.nodeName}
								</td>
								<td class="px-6 py-4">
									<div class="flex flex-wrap gap-1.5">
										{#each nodeRoles(node) as role (role)}
											<span class="{chipClass} bg-primary-500/10 text-primary-600 dark:text-primary-400">
												{role}
											</span>
										{/each}
									</div>
								</td>
								<td class="px-6 py-4">
									<div class="flex flex-wrap items-center gap-1.5">
										{#if ready !== null}
											<span
												class="{chipClass} {toneClasses[ready.tone]}"
												title={node.readySince === null ? undefined : `Since ${formatDateTime(node.readySince)}`}
											>
												{ready.label}
											</span>
										{:else}
											<span class="text-surface-500 dark:text-surface-400">---</span>
										{/if}
										{#each node.conditions as condition (condition.type)}
											<span class="{chipClass} {toneClasses.warn}">{conditionLabel(condition)}</span>
										{/each}
										{#if node.unschedulable}
											<span class="{chipClass} {toneClasses.neutral}">
												{node.unschedulableSince === null
													? 'Cordoned'
													: `Cordoned since ${formatDateTime(node.unschedulableSince)}`}
											</span>
										{/if}
									</div>
								</td>
								<td class="px-6 py-4 font-mono text-xs text-surface-700 dark:text-surface-300">
									{node.kubeletVersion ?? '---'}
									{#if node.kubeletSkew}
										<span
											title="The kubelet is more than three minor versions behind the cluster{cluster.clusterVersion === null
												? ''
												: ` (${cluster.clusterVersion})`}, or newer than it."
											class="{chipClass} ml-2 gap-1 font-sans {toneClasses.warn}"
										>
											<TriangleAlert size={12} />
											Version skew
										</span>
									{/if}
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">{podsLabel(node)}</td>
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
										<span class="{chipClass} {toneClasses.warn}">CA mismatch</span>
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
	{/if}

	{#if cluster.candidates.length > 0}
		<section data-testid="reporters-table" class={cardClass}>
			<h2 class="px-6 pt-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
				Reporters
			</h2>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th scope="col" class={headerClass}>Machine</th>
							<th scope="col" class={headerClass}>Status</th>
							<th scope="col" class={headerClass}>Error</th>
							<th scope="col" class={headerClass}>Last report</th>
							{#if canAdmin}
								<th scope="col" class={headerClass}><span class="sr-only">Actions</span></th>
							{/if}
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each cluster.candidates as candidate (candidate.machineId)}
							{@const block = candidateBlock(candidate)}
							<tr class="transition hover:bg-surface-100/60 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4">
									<a
										href="/machines/{candidate.machineId}"
										class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
									>
										{candidate.machineName}
									</a>
									{#if candidate.isActiveReporter}
										<span class="{chipClass} ml-2 {toneClasses.ok}">Reporting</span>
									{/if}
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{candidate.online ? 'Online' : 'Offline'}
								</td>
								<td class="px-6 py-4">
									{#if candidate.reporterError === null}
										<span class="text-surface-500 dark:text-surface-400">---</span>
									{:else}
										<span title={candidate.reporterError} class="text-red-700 dark:text-red-400">
											{describeReporterError(candidate.reporterError)}
										</span>
									{/if}
								</td>
								<td class="px-6 py-4 text-surface-500 dark:text-surface-400">
									{formatRelativeTime(candidate.lastReportAt)}
								</td>
								{#if canAdmin}
									<td class="px-6 py-4">
										<button
											type="button"
											onclick={() => unenrolOne(candidate)}
											disabled={busy || noKeys || block !== null}
											class={rowButtonClass}
										>
											Unenrol
										</button>
										{#if block !== null}
											<p class="mt-1 text-xs text-amber-800 dark:text-amber-300">{describeSkip(block)}</p>
										{/if}
									</td>
								{/if}
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>
	{/if}

	{#if eligibleToEnrol.length > 0}
		<section data-testid="eligible-table" class={cardClass}>
			<h2 class="px-6 pt-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
				Machines that can report
			</h2>
			<div class="overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th scope="col" class={headerClass}>Machine</th>
							<th scope="col" class={headerClass}>Status</th>
							<th scope="col" class={headerClass}>Remote commands</th>
							{#if canAdmin}
								<th scope="col" class={headerClass}><span class="sr-only">Actions</span></th>
							{/if}
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each eligibleToEnrol as machine (machine.machineId)}
							{@const block = rowBlock(machine)}
							<tr class="transition hover:bg-surface-100/60 dark:hover:bg-surface-700/50">
								<td class="px-6 py-4">
									<a
										href="/machines/{machine.machineId}"
										class="font-semibold text-primary-500 hover:text-primary-600 hover:underline dark:text-primary-400 dark:hover:text-primary-300"
									>
										{machine.machineName}
									</a>
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{machine.isPaused ? 'Paused' : machine.online ? 'Online' : 'Offline'}
								</td>
								<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
									{machine.remoteCommandsEnabled === null
										? 'Unknown'
										: machine.remoteCommandsEnabled
											? 'Enabled'
											: 'Disabled'}
								</td>
								{#if canAdmin}
									<td class="px-6 py-4">
										<button
											type="button"
											onclick={() => enrolOne(machine)}
											disabled={busy || noKeys || block !== null}
											class={rowButtonClass}
										>
											Enrol
										</button>
										{#if block !== null}
											<p class="mt-1 text-xs text-amber-800 dark:text-amber-300">{describeSkip(block)}</p>
										{/if}
									</td>
								{/if}
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</section>
	{/if}
</div>

{#if dialog !== null}
	<ReportingDialog
		title={dialog.kind === 'enable' ? 'Enable cluster reporting' : 'Disable cluster reporting'}
		paragraphs={dialog.kind === 'enable' ? [enableText, requirementsText] : disableText}
		plan={dialog.plan}
		hasKeys={usableKeys.length > 0}
		phase={dialog.phase}
		results={dialog.results}
		confirmLabel={dialog.kind === 'enable' ? 'Enable reporting' : 'Disable reporting'}
		blocked={dialogBlocked}
		onconfirm={confirmDialog}
		onclose={() => (dialog = null)}
	/>
{/if}
