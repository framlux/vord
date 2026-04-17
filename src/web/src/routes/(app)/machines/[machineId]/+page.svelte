<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { onMount } from 'svelte';
	import { untrack } from 'svelte';
	import type {
		MachineDto,
		MachineStatusDto,
		MachineTelemetryDto,
		MachineCertificateDto,
		MachineDetailDto,
		CommandDto,
		SigningKeyDto
	} from '$lib/api/types';
	import { MachineHealthStatus } from '$lib/api/types';
	import { ApiClient } from '$lib/api/client';
	import { formatDate, formatDateTime, formatRelativeTime, formatBytes, formatUptime } from '$lib/utils/format';
	import { getOsName } from '$lib/utils/enums';
	import EmptyState from '$lib/components/EmptyState.svelte';
	import HealthBadge from '$lib/components/HealthBadge.svelte';
	import MachineHero from '$lib/components/machine/MachineHero.svelte';
	import VitalsBar from '$lib/components/machine/VitalsBar.svelte';
	import { Terminal } from 'lucide-svelte';
	import {
		generateNonce,
		buildCanonicalPayload,
		signPayload,
		getLocalKeys
	} from '$lib/crypto/signing';

	let { data } = $props();

	let liveStatus = $state<MachineStatusDto | null>(null);
	const machine: MachineDto = $derived(data.machine);
	const isOnline = $derived(liveStatus?.isOnline ?? machine.isOnline);
	const lastPing = $derived(liveStatus?.lastPing ?? machine.lastPing);
	const commandsEnabled = $derived(liveStatus?.commandsEnabled ?? machine.commandsEnabled);
	const telemetryLatest: MachineTelemetryDto[] = $derived(data.telemetryLatest);
	const certificates: MachineCertificateDto[] = $derived(data.certificates);
	const machineDetail: MachineDetailDto | null = $derived(data.machineDetail);

	let pollFailures = $state(0);
	const POLL_FAILURE_THRESHOLD = 3;
	const showPollWarning = $derived(pollFailures >= POLL_FAILURE_THRESHOLD);

	let showSecurityOnly = $state(false);

	const filteredPackages = $derived.by(() => {
		const updates = machineDetail?.packageUpdates?.updates ?? [];
		if (showSecurityOnly) {
			return updates.filter(p => p.isSecurityUpdate);
		}

		return updates;
	});

	// Vitals derived values
	const cpuPercent = $derived(machineDetail?.cpuUsage?.cpuUsagePercent ?? null);
	const memoryPercent = $derived(machineDetail?.memoryUsage?.memoryUsagePercent ?? null);
	const maxDiskPercent = $derived.by(() => {
		const disks = machineDetail?.diskUsages?.disks;
		if (disks === undefined || disks === null || disks.length === 0) return null;

		return Math.max(...disks.map(d => d.usagePercent));
	});
	const diskTooltip = $derived.by(() => {
		const disks = machineDetail?.diskUsages?.disks;
		if (disks === undefined || disks === null || disks.length === 0) return undefined;

		return disks.map(d => `${d.path}: ${Math.round(d.usagePercent)}%`).join('\n');
	});

	onMount(() => {
		const api = new ApiClient('');
		const interval = setInterval(async () => {
			try {
				liveStatus = await api.getMachineStatus(machine.id);
				pollFailures = 0;
			} catch {
				pollFailures += 1;
			}
		}, 15_000);

		return () => clearInterval(interval);
	});

	let activeTab = $state<'overview' | 'telemetry' | 'certificates' | 'hardware' | 'packages' | 'commands'>('overview');

	const tabs = [
		{ id: 'overview' as const, label: 'Overview' },
		{ id: 'hardware' as const, label: 'Hardware' },
		{ id: 'packages' as const, label: 'Packages' },
		{ id: 'telemetry' as const, label: 'Telemetry' },
		{ id: 'certificates' as const, label: 'Certificates' },
		{ id: 'commands' as const, label: 'Commands' }
	];

	// Commands tab state
	let commands = $state<CommandDto[]>([]);
	let commandsLoading = $state(false);
	let commandType = $state('reboot');
	let sendingCommand = $state(false);
	let commandError = $state('');
	let commandSuccess = $state('');
	let localKeys = $state<Array<{ id: number; label: string; publicKeyBase64: string }>>([]);
	let selectedLocalKeyId = $state<number | null>(null);
	let serverSigningKeys = $state<SigningKeyDto[]>([]);

	const commandTypes = [
		{ value: 'reboot', label: 'Reboot' },
		{ value: 'kill_process', label: 'Kill Process' },
		{ value: 'kill_session', label: 'Kill Session' },
		{ value: 'check_updates', label: 'Check Updates' },
		{ value: 'install_updates', label: 'Install Updates' }
	];

	async function loadCommands() {
		commandsLoading = true;
		try {
			const api = new ApiClient('');
			commands = await api.getCommandHistory(machine.id);
		} catch {
			commands = [];
		} finally {
			commandsLoading = false;
		}
	}

	async function loadLocalKeys() {
		try {
			const userId = data.user?.id;
			const tenantId = data.user?.activeTenantId;
			if (userId && tenantId) {
				localKeys = await getLocalKeys(userId, tenantId);
				if (localKeys.length > 0 && selectedLocalKeyId === null) {
					selectedLocalKeyId = localKeys[0].id;
				}
			}
		} catch {
			localKeys = [];
		}
	}

	async function loadServerSigningKeys() {
		try {
			const api = new ApiClient('');
			const resp = await api.getSigningKeys();
			serverSigningKeys = resp.keys.filter((k) => k.revokedAt === null);
		} catch {
			serverSigningKeys = [];
		}
	}

	function findMatchingServerKey(localPublicKeyBase64: string): SigningKeyDto | undefined {
		return serverSigningKeys.find((sk) => sk.publicKey === localPublicKeyBase64);
	}

	async function sendCommand() {
		commandError = '';
		commandSuccess = '';

		if (selectedLocalKeyId === null) {
			commandError = 'No signing key selected. Generate one in Settings > Signing Keys.';

			return;
		}

		const localKey = localKeys.find((k) => k.id === selectedLocalKeyId);
		if (localKey === undefined) {
			commandError = 'Selected key not found.';

			return;
		}

		const serverKey = findMatchingServerKey(localKey.publicKeyBase64);
		if (serverKey === undefined) {
			commandError = 'This key is not registered on the server. Re-register it in Settings > Signing Keys.';

			return;
		}

		sendingCommand = true;
		try {
			const api = new ApiClient('');
			const cmdId = crypto.randomUUID();
			const nonce = generateNonce();
			const now = new Date();
			const expiresAt = new Date(now.getTime() + 10 * 60 * 1000);
			const timestamp = now.toISOString();
			const expiresAtStr = expiresAt.toISOString();

			const userId = data.user?.id ?? 0;
			const tenantId = data.user?.activeTenantId ?? 0;

			const canonical = buildCanonicalPayload({
				command_id: cmdId,
				command_type: commandType,
				expires_at: expiresAtStr,
				machine_id: machine.id,
				nonce,
				params: null,
				tenant_id: tenantId,
				timestamp,
				user_id: userId
			});

			const signature = await signPayload(selectedLocalKeyId, canonical);

			await api.sendCommand({
				commandId: cmdId,
				machineId: machine.id,
				signingKeyId: serverKey.id,
				commandType,
				nonce,
				signature,
				canonicalPayload: canonical,
				timestamp,
				expiresAt: expiresAtStr
			});

			commandSuccess = `${commandType} command sent successfully.`;
			await loadCommands();
		} catch (err: unknown) {
			commandError = err instanceof Error ? err.message : 'Failed to send command';
		} finally {
			sendingCommand = false;
		}
	}

	function getStatusBadgeClasses(status: string): string {
		switch (status) {
			case 'Pending':
				return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';
			case 'Delivered':
				return 'bg-indigo-100 text-indigo-800 dark:bg-indigo-900/30 dark:text-indigo-400';
			case 'Executed':
				return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400';
			case 'Failed':
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
			case 'Expired':
				return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400';
			case 'Rejected':
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
			default:
				return 'bg-surface-100 text-surface-800 dark:bg-surface-700 dark:text-surface-300';
		}
	}

	$effect(() => {
		if (activeTab === 'commands') {
			untrack(() => {
				loadCommands();
				loadLocalKeys();
				loadServerSigningKeys();
			});
		}
	});

	function getCertificateStatus(cert: MachineCertificateDto): { label: string; classes: string } {
		if (cert.revokedAt) {
			return {
				label: 'Revoked',
				classes:
					'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400'
			};
		}
		const now = new Date();
		const expires = new Date(cert.expiresAt);
		if (expires < now) {
			return {
				label: 'Expired',
				classes:
					'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400'
			};
		}

		return {
			label: 'Active',
			classes:
				'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400'
		};
	}
</script>

<svelte:head><title>Machine Detail - Vord</title></svelte:head>

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

	<!-- Header -->
	<MachineHero {machine} detail={machineDetail} {isOnline} {lastPing} />

	<!-- Vitals -->
	<VitalsBar
		cpuPercent={cpuPercent}
		memoryPercent={memoryPercent}
		maxDiskPercent={maxDiskPercent}
		diskDetails={diskTooltip}
	/>

	<!-- Tabs -->
	<div class="border-b border-surface-200 dark:border-surface-700">
		<nav class="-mb-px flex gap-6">
			{#each tabs as tab}
				<button
					onclick={() => (activeTab = tab.id)}
					class="border-b-2 px-1 py-3 text-sm font-medium transition {activeTab === tab.id
						? 'border-primary-500 text-primary-500'
						: 'border-transparent text-surface-500 hover:border-surface-300 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
				>
					{tab.label}
				</button>
			{/each}
		</nav>
	</div>

	<!-- Tab Content -->
	<div>
	{#if activeTab === 'overview'}
		<div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
			<!-- Identity Card -->
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-5 dark:border-surface-700 dark:bg-surface-800">
				<h3 class="mb-4 text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Identity</h3>
				<div class="space-y-3">
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Name</span>
						<span class="text-sm font-medium text-surface-900 dark:text-surface-100">{machine.name}</span>
					</div>
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Hostname</span>
						<span class="font-mono text-sm text-surface-900 dark:text-surface-100">{machine.hostname}</span>
					</div>
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Serial Number</span>
						<span class="font-mono text-sm text-surface-900 dark:text-surface-100">{machine.serialNumber}</span>
					</div>
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Asset Tag</span>
						<span class="text-sm text-surface-900 dark:text-surface-100">{machine.assetTag ?? '---'}</span>
					</div>
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Description</span>
						<span class="max-w-48 text-right text-sm text-surface-900 dark:text-surface-100">{machine.description ?? '---'}</span>
					</div>
				</div>
			</div>

			<!-- Network & OS Card -->
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-5 dark:border-surface-700 dark:bg-surface-800">
				<h3 class="mb-4 text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Network & OS</h3>
				<div class="space-y-3">
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Operating System</span>
						<span class="text-sm font-medium text-surface-900 dark:text-surface-100">{getOsName(machine.operatingSystem)}</span>
					</div>
					{#if machineDetail?.osVersion}
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">OS Version</span>
							<span class="font-mono text-sm text-surface-900 dark:text-surface-100">{machineDetail.osVersion.version}</span>
						</div>
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Kernel</span>
							<span class="font-mono text-sm text-surface-900 dark:text-surface-100">{machineDetail.osVersion.build || '---'}</span>
						</div>
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Arch</span>
							<span class="font-mono text-sm text-surface-900 dark:text-surface-100">{machineDetail.osVersion.arch}</span>
						</div>
					{/if}
					{#if machineDetail?.systemInfo?.ipAddresses && machineDetail.systemInfo.ipAddresses.length > 0}
						<div>
							<span class="text-sm text-surface-500 dark:text-surface-400">IP Addresses</span>
							<div class="mt-1.5 flex flex-wrap gap-1.5">
								{#each machineDetail.systemInfo.ipAddresses as ip}
									<span class="rounded bg-surface-100 px-2 py-0.5 font-mono text-xs text-surface-700 dark:bg-surface-700 dark:text-surface-300">
										{ip}
									</span>
								{/each}
							</div>
						</div>
					{/if}
				</div>
			</div>

			<!-- Uptime & Timing Card -->
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-5 dark:border-surface-700 dark:bg-surface-800">
				<h3 class="mb-4 text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Timing</h3>
				<div class="space-y-3">
					{#if machineDetail?.systemInfo}
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Uptime</span>
							<span class="text-sm font-medium text-surface-900 dark:text-surface-100">{formatUptime(machineDetail.systemInfo.uptimeSeconds)}</span>
						</div>
					{/if}
					<div class="flex justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Registered</span>
						<span class="text-sm text-surface-900 dark:text-surface-100">{formatDate(machine.registeredOn)}</span>
					</div>
					{#if lastPing}
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Last Seen</span>
							<span class="text-sm text-surface-900 dark:text-surface-100">{formatRelativeTime(lastPing)}</span>
						</div>
					{/if}
					{#if machineDetail?.telemetryLastUpdated}
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Last Telemetry</span>
							<span class="text-sm text-surface-900 dark:text-surface-100">{formatRelativeTime(machineDetail.telemetryLastUpdated)}</span>
						</div>
					{/if}
				</div>
			</div>

			<!-- Health Summary Card -->
			<div class="rounded-xl border border-surface-200 bg-surface-50 p-5 dark:border-surface-700 dark:bg-surface-800">
				<h3 class="mb-4 text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Health</h3>
				<div class="space-y-3">
					<div class="flex items-center justify-between">
						<span class="text-sm text-surface-500 dark:text-surface-400">Status</span>
						<HealthBadge status={machineDetail?.healthStatus ?? MachineHealthStatus.Offline} />
					</div>
					{#if machineDetail}
						<div class="flex justify-between">
							<span class="text-sm text-surface-500 dark:text-surface-400">Services</span>
							<span class="text-sm text-surface-900 dark:text-surface-100">
								{#if (machineDetail.failedServices?.length ?? 0) > 0}
									<span class="font-medium text-red-600 dark:text-red-400">{machineDetail.failedServices.length} failed</span>
									/ {machineDetail.totalServices}
								{:else}
									{machineDetail.totalServices} running
								{/if}
							</span>
						</div>
						{#if machineDetail.packageUpdates}
							<div class="flex justify-between">
								<span class="text-sm text-surface-500 dark:text-surface-400">Pending Updates</span>
								<span class="text-sm text-surface-900 dark:text-surface-100">{machineDetail.packageUpdates.updates?.length ?? 0}</span>
							</div>
							{@const securityCount = (machineDetail.packageUpdates.updates ?? []).filter(u => u.isSecurityUpdate).length}
							{#if securityCount > 0}
								<div class="flex justify-between">
									<span class="text-sm text-surface-500 dark:text-surface-400">Security Updates</span>
									<span class="text-sm font-medium text-red-600 dark:text-red-400">{securityCount}</span>
								</div>
							{/if}
						{/if}
					{/if}
				</div>
			</div>
		</div>

		<!-- Failed Services (if any) -->
		{#if machineDetail && (machineDetail.failedServices?.length ?? 0) > 0}
			<div class="mt-4 rounded-xl border border-red-200 bg-red-50/50 p-5 dark:border-red-900/30 dark:bg-red-900/10">
				<h3 class="mb-3 text-sm font-semibold text-red-800 dark:text-red-400">Failed Services</h3>
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead>
							<tr class="border-b border-red-200 dark:border-red-900/30">
								<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-red-600 dark:text-red-400">Unit</th>
								<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-red-600 dark:text-red-400">State</th>
								<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-red-600 dark:text-red-400">Description</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-red-100 dark:divide-red-900/20">
							{#each (machineDetail.failedServices ?? []) as svc}
								<tr>
									<td class="py-2 pr-4 font-mono text-xs text-red-700 dark:text-red-300">{svc.unit}</td>
									<td class="py-2 pr-4 text-xs text-red-600 dark:text-red-400">{svc.activeState}/{svc.subState}</td>
									<td class="py-2 text-xs text-red-600 dark:text-red-400">{svc.description}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>
		{/if}

		<!-- Recent SSH Sessions (if any) -->
		{#if machineDetail && (machineDetail.recentSshSessions?.length ?? 0) > 0}
			<div class="mt-4 rounded-xl border border-surface-200 bg-surface-50 p-5 dark:border-surface-700 dark:bg-surface-800">
				<h3 class="mb-3 text-xs font-semibold uppercase tracking-wider text-surface-400 dark:text-surface-500">Recent SSH Sessions</h3>
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead>
							<tr class="border-b border-surface-200 dark:border-surface-700">
								<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">User</th>
								<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Source</th>
								<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Action</th>
								<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">When</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
							{#each (machineDetail.recentSshSessions ?? []) as session}
								<tr>
									<td class="py-2 pr-4 font-mono text-xs text-surface-700 dark:text-surface-300">{session.user}</td>
									<td class="py-2 pr-4 font-mono text-xs text-surface-600 dark:text-surface-400">{session.sourceIp}</td>
									<td class="py-2 pr-4 text-xs text-surface-600 dark:text-surface-400">{session.action}</td>
									<td class="py-2 text-xs text-surface-500 dark:text-surface-400">{formatRelativeTime(session.timestamp)}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>
		{/if}

	{:else if activeTab === 'telemetry'}
		{#if telemetryLatest.length === 0}
			<EmptyState
				title="No telemetry data"
				description="Telemetry data will appear here once the agent reports in."
			/>
		{:else}
			<div
				class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
			>
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead>
							<tr class="border-b border-surface-200 dark:border-surface-700">
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
									Payload
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Received At
								</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
							{#each telemetryLatest as entry}
								<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
									<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
										{String(entry.telemetryType)}
									</td>
									<td
										class="max-w-md px-6 py-4 font-mono text-xs text-surface-600 dark:text-surface-400"
										title={entry.payload}
									>
										{entry.payload.length > 100
											? entry.payload.slice(0, 100) + '...'
											: entry.payload}
									</td>
									<td class="px-6 py-4 text-surface-500 dark:text-surface-400">
										{formatDateTime(entry.receivedAt)}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>
		{/if}
	{:else if activeTab === 'hardware'}
		{#if machineDetail === null || machineDetail.systemInfo === null}
			<EmptyState
				title="No hardware data"
				description="Hardware information will appear here once the agent reports in."
			/>
		{:else}
			<div class="space-y-6">
				<!-- System Info -->
				<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
					<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">System Info</h3>
					<div class="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">CPU</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{machineDetail.systemInfo.cpuBrand}
							</p>
							<p class="text-xs text-surface-500 dark:text-surface-400">
								{machineDetail.systemInfo.cpuPhysicalCores} physical / {machineDetail.systemInfo.cpuLogicalCores} logical cores
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Memory</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{formatBytes(machineDetail.systemInfo.physicalMemory)}
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Hardware Vendor</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{machineDetail.systemInfo.hardwareVendor || '---'}
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Hardware Model</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{machineDetail.systemInfo.hardwareModel || '---'}
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Serial Number</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{machineDetail.systemInfo.hardwareSerial || '---'}
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">BIOS Version</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{machineDetail.systemInfo.biosVersion || '---'}
							</p>
						</div>
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Uptime</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{formatUptime(machineDetail.systemInfo.uptimeSeconds)}
							</p>
						</div>
						{#if (machineDetail.systemInfo.ipAddresses?.length ?? 0) > 0}
							<div class="sm:col-span-2">
								<p class="text-xs text-surface-500 dark:text-surface-400">IP Addresses</p>
								<div class="mt-1 flex flex-wrap gap-2">
									{#each (machineDetail.systemInfo.ipAddresses ?? []) as ip}
										<span class="rounded bg-surface-100 px-2 py-0.5 font-mono text-xs text-surface-700 dark:bg-surface-700 dark:text-surface-300">
											{ip}
										</span>
									{/each}
								</div>
							</div>
						{/if}
					</div>
				</div>

				<!-- Disk Usage -->
				{#if machineDetail.diskUsages && (machineDetail.diskUsages.disks?.length ?? 0) > 0}
					<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
						<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">Disk Usage</h3>
						<div class="overflow-x-auto">
							<table class="w-full text-left text-sm">
								<thead>
									<tr class="border-b border-surface-200 dark:border-surface-700">
										<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Mount</th>
										<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Device</th>
										<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Usage</th>
										<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">%</th>
									</tr>
								</thead>
								<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
									{#each (machineDetail.diskUsages?.disks ?? []) as disk}
										<tr class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
											<td class="py-3 pr-4 font-mono text-xs text-surface-700 dark:text-surface-300">{disk.path}</td>
											<td class="py-3 pr-4 font-mono text-xs text-surface-600 dark:text-surface-400">{disk.device}</td>
											<td class="py-3 pr-4">
												<div class="h-2 w-24 overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700">
													<div
														class="h-full rounded-full transition-all {disk.usagePercent >= 95 ? 'bg-red-500' : disk.usagePercent >= 80 ? 'bg-amber-500' : 'bg-green-500'}"
														style="width: {Math.min(disk.usagePercent, 100)}%"
													></div>
												</div>
											</td>
											<td class="py-3 text-sm font-medium text-surface-700 dark:text-surface-300">{Math.round(disk.usagePercent)}%</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</div>
				{/if}

				<!-- Hardware Health -->
				{#if machineDetail.hardwareHealth !== null}
					<div class="space-y-4">
						<!-- Disk SMART -->
						{#if (machineDetail.hardwareHealth.diskSmart?.length ?? 0) > 0}
							<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
								<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">Disk SMART</h3>
								<div class="overflow-x-auto">
									<table class="w-full text-left text-sm">
										<thead>
											<tr class="border-b border-surface-200 dark:border-surface-700">
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Device</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Model</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Health</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Temp</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Wearout</th>
												<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Power-On Hours</th>
											</tr>
										</thead>
										<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
											{#each (machineDetail.hardwareHealth.diskSmart ?? []) as disk}
												<tr class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
													<td class="py-3 pr-4 font-mono text-xs text-surface-700 dark:text-surface-300">{disk.device}</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{disk.model}</td>
													<td class="py-3 pr-4">
														<span class="inline-flex rounded-full px-2 py-0.5 text-xs font-medium {disk.healthStatus === 'PASSED' ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400'}">
															{disk.healthStatus}
														</span>
													</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{disk.temperatureCelsius}°C</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{disk.wearoutPercent}%</td>
													<td class="py-3 text-surface-700 dark:text-surface-300">{disk.powerOnHours.toLocaleString()} h</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}

						<!-- Fans -->
						{#if (machineDetail.hardwareHealth.fans?.length ?? 0) > 0}
							<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
								<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">Fans</h3>
								<div class="overflow-x-auto">
									<table class="w-full text-left text-sm">
										<thead>
											<tr class="border-b border-surface-200 dark:border-surface-700">
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Name</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">RPM</th>
												<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Status</th>
											</tr>
										</thead>
										<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
											{#each (machineDetail.hardwareHealth.fans ?? []) as fan}
												<tr class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{fan.name}</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{fan.rpm.toLocaleString()}</td>
													<td class="py-3 text-surface-700 dark:text-surface-300">{fan.status}</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}

						<!-- Power Supplies -->
						{#if (machineDetail.hardwareHealth.powerSupplies?.length ?? 0) > 0}
							<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
								<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">Power Supplies</h3>
								<div class="overflow-x-auto">
									<table class="w-full text-left text-sm">
										<thead>
											<tr class="border-b border-surface-200 dark:border-surface-700">
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Name</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Watts</th>
												<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Status</th>
											</tr>
										</thead>
										<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
											{#each (machineDetail.hardwareHealth.powerSupplies ?? []) as psu}
												<tr class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{psu.name}</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{psu.watts} W</td>
													<td class="py-3 text-surface-700 dark:text-surface-300">{psu.status}</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}

						<!-- Temperatures -->
						{#if (machineDetail.hardwareHealth.temperatures?.length ?? 0) > 0}
							<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
								<h3 class="mb-4 text-base font-semibold text-surface-900 dark:text-surface-50">Temperatures</h3>
								<div class="overflow-x-auto">
									<table class="w-full text-left text-sm">
										<thead>
											<tr class="border-b border-surface-200 dark:border-surface-700">
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Name</th>
												<th class="pb-2 pr-4 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Celsius</th>
												<th class="pb-2 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Status</th>
											</tr>
										</thead>
										<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
											{#each (machineDetail.hardwareHealth.temperatures ?? []) as temp}
												<tr class="hover:bg-surface-50 dark:hover:bg-surface-700/50">
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{temp.name}</td>
													<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">{temp.celsius}°C</td>
													<td class="py-3 text-surface-700 dark:text-surface-300">{temp.status}</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}
					</div>
				{/if}
			</div>
		{/if}
	{:else if activeTab === 'packages'}
		{#if machineDetail === null || machineDetail.packageUpdates === null}
			<EmptyState
				title="No package data"
				description="Package update information will appear here once the agent reports in."
			/>
		{:else}
			<div class="space-y-4">
				<div class="flex items-center justify-between">
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Package manager: <span class="font-medium text-surface-900 dark:text-surface-100">{machineDetail.packageUpdates.packageManager}</span>
					</p>
					<label class="flex cursor-pointer items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
						<input
							type="checkbox"
							bind:checked={showSecurityOnly}
							class="h-4 w-4 rounded border-surface-300 text-primary-600 focus:ring-primary-500 dark:border-surface-600"
						/>
						Security updates only
					</label>
				</div>
				{#if filteredPackages.length === 0}
					<EmptyState
						title="No packages"
						description={showSecurityOnly ? 'No security updates pending.' : 'No package updates available.'}
					/>
				{:else}
					<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
						<div class="overflow-x-auto">
							<table class="w-full text-left text-sm">
								<thead>
									<tr class="border-b border-surface-200 dark:border-surface-700">
										<th class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Package</th>
										<th class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Current Version</th>
										<th class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Available Version</th>
										<th class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">Security</th>
									</tr>
								</thead>
								<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
									{#each filteredPackages as pkg}
										<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
											<td class="px-6 py-3 font-medium text-surface-900 dark:text-surface-100">{pkg.name}</td>
											<td class="px-6 py-3 font-mono text-xs text-surface-600 dark:text-surface-400">{pkg.currentVersion}</td>
											<td class="px-6 py-3 font-mono text-xs text-surface-600 dark:text-surface-400">{pkg.availableVersion}</td>
											<td class="px-6 py-3">
												{#if pkg.isSecurityUpdate}
													<span class="inline-flex items-center rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-800 dark:bg-red-900/30 dark:text-red-400">
														Security
													</span>
												{/if}
											</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</div>
				{/if}
			</div>
		{/if}
	{:else if activeTab === 'certificates'}
		{#if certificates.length === 0}
			<EmptyState
				title="No certificates"
				description="Certificate information will appear here once certificates are issued."
			/>
		{:else}
			<div
				class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
			>
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead>
							<tr class="border-b border-surface-200 dark:border-surface-700">
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Thumbprint
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Issued At
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Expires At
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Revoked At
								</th>
								<th
									scope="col"
									class="px-6 py-3 text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400"
								>
									Status
								</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
							{#each certificates as cert}
								{@const status = getCertificateStatus(cert)}
								<tr class="transition hover:bg-surface-50 dark:hover:bg-surface-700/50">
									<td
										class="px-6 py-4 font-mono text-xs text-surface-700 dark:text-surface-300"
										title={cert.thumbprint}
									>
										{cert.thumbprint.length > 20
											? cert.thumbprint.slice(0, 20) + '...'
											: cert.thumbprint}
									</td>
									<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
										{formatDateTime(cert.issuedAt)}
									</td>
									<td class="px-6 py-4 text-surface-700 dark:text-surface-300">
										{formatDateTime(cert.expiresAt)}
									</td>
									<td class="px-6 py-4 text-surface-500 dark:text-surface-400">
										{formatDateTime(cert.revokedAt)}
									</td>
									<td class="px-6 py-4">
										<span
											class="inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium {status.classes}"
											aria-label="Certificate status: {status.label}"
										>
											{status.label}
										</span>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			</div>
		{/if}
	{:else if activeTab === 'commands'}
		<!-- Send Command Form -->
		<div class="rounded-lg border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
			<div class="flex items-center gap-2">
				<Terminal size={20} class="text-primary-500" />
				<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Send Command</h3>
			</div>
			<p class="mt-1 text-sm text-surface-500">
				Commands are signed with your Ed25519 key and verified by the agent.
			</p>

			{#if commandsEnabled === false}
				<div class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-700 dark:bg-amber-900/20">
					<p class="text-sm text-amber-700 dark:text-amber-300">
						Remote commands are disabled on this machine. The agent must be configured with
						<code class="rounded bg-amber-100 px-1 py-0.5 font-mono text-xs dark:bg-amber-800/40">allow_remote_commands = true</code>
						to accept commands.
					</p>
				</div>
			{:else if localKeys.length === 0}
				<div class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-700 dark:bg-amber-900/20">
					<p class="text-sm text-amber-700 dark:text-amber-300">
						No signing keys found on this device.
						<a href="/settings/signing-keys" class="underline hover:text-amber-800 dark:hover:text-amber-200">
							Generate a signing key
						</a> first.
					</p>
				</div>
			{:else}
				<div class="mt-4 flex items-end gap-3">
					<div class="w-48">
						<label for="cmd-type" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Command</label>
						<select
							id="cmd-type"
							bind:value={commandType}
							class="mt-1 w-full rounded-lg border border-surface-300 bg-white px-4 py-2 text-sm text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
						>
							{#each commandTypes as ct}
								<option value={ct.value}>{ct.label}</option>
							{/each}
						</select>
					</div>
					<div class="flex-1">
						<label for="signing-key" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Signing Key</label>
						<select
							id="signing-key"
							bind:value={selectedLocalKeyId}
							class="mt-1 w-full rounded-lg border border-surface-300 bg-white px-4 py-2 text-sm text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
						>
							{#each localKeys as lk}
								<option value={lk.id}>{lk.label}</option>
							{/each}
						</select>
					</div>
					<button
						onclick={sendCommand}
						disabled={sendingCommand}
						class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
					>
						{sendingCommand ? 'Sending...' : 'Send'}
					</button>
				</div>
			{/if}

			{#if commandError}
				<p class="mt-3 text-sm text-red-600 dark:text-red-400">{commandError}</p>
			{/if}
			{#if commandSuccess}
				<p class="mt-3 text-sm text-green-600 dark:text-green-400">{commandSuccess}</p>
			{/if}
		</div>

		<!-- Command History -->
		<div class="mt-4 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
			<div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
				<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Command History</h3>
			</div>
			{#if commandsLoading}
				<p class="px-6 py-8 text-center text-sm text-surface-500">Loading...</p>
			{:else if commands.length === 0}
				<p class="px-6 py-8 text-center text-sm text-surface-500">No commands sent to this machine yet.</p>
			{:else}
				<div class="overflow-x-auto">
					<table class="w-full text-left text-sm">
						<thead class="bg-surface-50 text-xs uppercase text-surface-500 dark:bg-surface-900">
							<tr>
								<th class="px-6 py-3">Type</th>
								<th class="px-6 py-3">Status</th>
								<th class="px-6 py-3">Sent</th>
								<th class="px-6 py-3">Result</th>
							</tr>
						</thead>
						<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
							{#each commands as cmd (cmd.id)}
								<tr class="hover:bg-surface-50 dark:hover:bg-surface-800/50">
									<td class="px-6 py-4 font-medium text-surface-900 dark:text-surface-50">
										{cmd.commandType}
									</td>
									<td class="px-6 py-4">
										<span class="inline-flex rounded-full px-2.5 py-0.5 text-xs font-medium {getStatusBadgeClasses(cmd.status)}">
											{cmd.status}
										</span>
									</td>
									<td class="px-6 py-4 text-surface-500">{formatDateTime(cmd.createdAt)}</td>
									<td class="px-6 py-4 text-surface-500">
										{#if cmd.exitCode !== null}
											Exit: {cmd.exitCode}
										{:else if cmd.resultMessage}
											{cmd.resultMessage}
										{:else}
											-
										{/if}
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>
			{/if}
		</div>
	{/if}
	</div>
</div>
