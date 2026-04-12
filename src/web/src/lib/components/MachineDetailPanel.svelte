<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { untrack } from 'svelte';
	import type { MachineDetailDto } from '$lib/api/types';
	import { ApiClient } from '$lib/api/client';
	import HealthBadge from './HealthBadge.svelte';
	import ProgressBar from './ProgressBar.svelte';
	import { formatBytes, formatUptime, formatRelativeTime, formatDateTime } from '$lib/utils/format';
	import { TEMP_WARNING_CELSIUS, TEMP_CRITICAL_CELSIUS } from '$lib/utils/constants';
	import { X, Clock } from 'lucide-svelte';

	let {
		machineId,
		onclose
	}: { machineId: number | null; onclose: () => void } = $props();

	let detail = $state<MachineDetailDto | null>(null);
	let loading = $state(false);
	let error = $state<string | null>(null);
	let requestId = 0;

	const api = new ApiClient('');

	$effect(() => {
		if (machineId !== null) {
			untrack(() => loadDetail(machineId!));
		} else {
			detail = null;
		}
	});

	async function loadDetail(id: number) {
		requestId += 1;
		const thisRequest = requestId;
		loading = true;
		error = null;
		try {
			const result = await api.getMachineDetail(id);
			if (thisRequest !== requestId) return;
			detail = result;
		} catch (e) {
			if (thisRequest !== requestId) return;
			error = 'Failed to load machine details';
			detail = null;
		} finally {
			if (thisRequest === requestId) {
				loading = false;
			}
		}
	}
</script>

<!-- Escape key handler -->
<svelte:window onkeydown={(e) => { if (e.key === 'Escape' && machineId !== null) onclose(); }} />

<!-- Backdrop -->
{#if machineId !== null}
	<div
		class="fixed inset-0 z-40 bg-black/30 transition-opacity"
		onclick={onclose}
		role="presentation"
	></div>

	<!-- Panel -->
	<div
		class="fixed inset-y-0 right-0 z-50 w-full max-w-lg overflow-y-auto border-l border-surface-200 bg-surface-50 shadow-xl dark:border-surface-700 dark:bg-surface-900"
		role="dialog"
		aria-modal="true"
		aria-label="Machine details"
	>
		{#if loading}
			<div class="flex h-full items-center justify-center">
				<div
					class="h-8 w-8 animate-spin rounded-full border-4 border-primary-500 border-t-transparent"
				></div>
			</div>
		{:else if error}
			<div class="p-6">
				<p class="text-red-500">{error}</p>
				<button class="mt-4 text-sm text-primary-500 hover:underline" onclick={onclose}>
					Close
				</button>
			</div>
		{:else if detail}
			<!-- Header -->
			<div
				class="sticky top-0 z-10 flex items-start justify-between border-b border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-900"
			>
				<div class="min-w-0">
					<h2 class="truncate text-lg font-bold text-surface-900 dark:text-surface-50">
						{detail.hostname ?? detail.name}
					</h2>
					{#if detail.systemInfo?.ipAddresses?.length}
						<p class="mt-0.5 text-sm text-surface-500 dark:text-surface-400">
							{detail.systemInfo.ipAddresses[0]}
						</p>
					{/if}
					{#if detail.systemInfo}
						<p class="mt-0.5 text-sm text-surface-400 dark:text-surface-500">
							{detail.systemInfo.hardwareVendor}
							{detail.systemInfo.hardwareModel}
						</p>
					{/if}
					<div class="mt-2">
						<HealthBadge status={detail.healthStatus} />
					</div>
				</div>
				<button
					class="ml-4 rounded-lg p-1.5 text-surface-400 hover:bg-surface-100 hover:text-surface-600 dark:hover:bg-surface-800 dark:hover:text-surface-300"
					onclick={onclose}
				>
					<X class="h-5 w-5" />
				</button>
			</div>

			<div class="space-y-6 p-6">
				{#if detail.telemetryLastUpdated === null || detail.telemetryLastUpdated === undefined}
					<div class="flex items-start gap-3 rounded-lg border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-950/40">
						<Clock class="mt-0.5 h-5 w-5 shrink-0 text-amber-500" />
						<div>
							<p class="text-sm font-medium text-amber-800 dark:text-amber-300">Waiting for telemetry</p>
							<p class="mt-0.5 text-xs text-amber-700 dark:text-amber-400">
								This machine has not reported any telemetry data yet. Data will appear here once the agent begins sending reports.
							</p>
						</div>
					</div>
				{/if}

				<!-- Quick Stats -->
				<div class="grid grid-cols-3 gap-4">
					<div class="rounded-lg border border-surface-200 p-3 dark:border-surface-700">
						<p class="text-xs text-surface-500 dark:text-surface-400">CPU</p>
						<p class="text-lg font-semibold text-surface-900 dark:text-surface-50">
							{detail.cpuUsage?.cpuUsagePercent ?? '—'}%
						</p>
					</div>
					<div class="rounded-lg border border-surface-200 p-3 dark:border-surface-700">
						<p class="text-xs text-surface-500 dark:text-surface-400">Memory</p>
						<p class="text-lg font-semibold text-surface-900 dark:text-surface-50">
							{detail.memoryUsage?.memoryUsagePercent ?? '—'}%
						</p>
					</div>
					<div class="rounded-lg border border-surface-200 p-3 dark:border-surface-700">
						<p class="text-xs text-surface-500 dark:text-surface-400">Uptime</p>
						<p class="text-lg font-semibold text-surface-900 dark:text-surface-50">
							{detail.systemInfo ? formatUptime(detail.systemInfo.uptimeSeconds) : '—'}
						</p>
					</div>
				</div>

				<!-- Hardware Health -->
				{#if detail.hardwareHealth}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-surface-700 dark:text-surface-300">
							Hardware Health
						</h3>
						<div class="space-y-3 text-sm">
							{#if detail.hardwareHealth.temperatures.length > 0}
								<div>
									<p
										class="mb-1 text-xs font-medium text-surface-500 dark:text-surface-400"
									>
										Temperatures
									</p>
									{#each detail.hardwareHealth.temperatures as temp}
										<div
											class="flex justify-between py-0.5 text-surface-700 dark:text-surface-300"
										>
											<span class="truncate">{temp.name}</span>
											<span
												class={temp.celsius >= TEMP_CRITICAL_CELSIUS
													? 'font-medium text-red-500'
													: temp.celsius >= TEMP_WARNING_CELSIUS
														? 'font-medium text-amber-500'
														: ''}
											>
												{temp.celsius.toFixed(0)}°C
											</span>
										</div>
									{/each}
								</div>
							{/if}

							{#if detail.hardwareHealth.fans.length > 0}
								<div>
									<p
										class="mb-1 text-xs font-medium text-surface-500 dark:text-surface-400"
									>
										Fans
									</p>
									{#each detail.hardwareHealth.fans as fan}
										<div
											class="flex justify-between py-0.5 text-surface-700 dark:text-surface-300"
										>
											<span class="truncate">{fan.name}</span>
											<span
												class={fan.rpm === 0 ? 'font-medium text-red-500' : ''}
											>
												{fan.rpm} RPM
											</span>
										</div>
									{/each}
								</div>
							{/if}

							{#if detail.hardwareHealth.powerSupplies.length > 0}
								<div>
									<p
										class="mb-1 text-xs font-medium text-surface-500 dark:text-surface-400"
									>
										Power Supplies
									</p>
									{#each detail.hardwareHealth.powerSupplies as psu}
										<div
											class="flex justify-between py-0.5 text-surface-700 dark:text-surface-300"
										>
											<span class="truncate">{psu.name}</span>
											<span>{psu.watts}W ({psu.status})</span>
										</div>
									{/each}
								</div>
							{/if}

							{#if detail.hardwareHealth.diskSmart.length > 0}
								<div>
									<p
										class="mb-1 text-xs font-medium text-surface-500 dark:text-surface-400"
									>
										Disk SMART
									</p>
									{#each detail.hardwareHealth.diskSmart as disk}
										<div
											class="flex items-center justify-between gap-2 py-0.5 text-surface-700 dark:text-surface-300"
										>
											<span class="min-w-0 truncate"
												>{disk.model || disk.device}</span
											>
											<span
												class="shrink-0 text-xs {disk.healthStatus === 'FAILED'
													? 'font-medium text-red-500'
													: ''}"
											>
												{disk.healthStatus}
												{#if disk.temperatureCelsius > 0}
													| {disk.temperatureCelsius}°C
												{/if}
												{#if disk.wearoutPercent > 0}
													| {disk.wearoutPercent}% worn
												{/if}
											</span>
										</div>
									{/each}
								</div>
							{/if}

							{#if detail.hardwareHealth.bmcFirmwareVersion}
								<p class="text-xs text-surface-400 dark:text-surface-500">
									BMC Firmware: {detail.hardwareHealth.bmcFirmwareVersion}
								</p>
							{/if}
						</div>
					</section>
				{/if}

				<!-- Storage -->
				{#if detail.diskUsages?.disks?.length}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-surface-700 dark:text-surface-300">
							Storage
						</h3>
						<div class="space-y-2">
							{#each detail.diskUsages.disks as disk}
								<div>
									<div
										class="mb-1 flex justify-between text-xs text-surface-500 dark:text-surface-400"
									>
										<span>{disk.path}</span>
										<span>{disk.device}</span>
									</div>
									<ProgressBar
										value={disk.usagePercent}
										label={formatBytes(disk.blocksUsed * disk.blocksSize)}
									/>
								</div>
							{/each}
						</div>
					</section>
				{/if}

				<!-- System Info -->
				{#if detail.systemInfo || detail.osVersion}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-surface-700 dark:text-surface-300">
							System Info
						</h3>
						<dl class="grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
							{#if detail.osVersion}
								<dt class="text-surface-500 dark:text-surface-400">OS</dt>
								<dd class="text-surface-900 dark:text-surface-100">
									{detail.osVersion.name} {detail.osVersion.version}
								</dd>
								{#if detail.osVersion.build}
									<dt class="text-surface-500 dark:text-surface-400">Kernel</dt>
									<dd class="truncate text-surface-900 dark:text-surface-100">
										{detail.osVersion.build}
									</dd>
								{/if}
							{/if}
							{#if detail.systemInfo}
								<dt class="text-surface-500 dark:text-surface-400">CPU</dt>
								<dd class="truncate text-surface-900 dark:text-surface-100">
									{detail.systemInfo.cpuBrand}
								</dd>
								<dt class="text-surface-500 dark:text-surface-400">Cores</dt>
								<dd class="text-surface-900 dark:text-surface-100">
									{detail.systemInfo.cpuPhysicalCores}
								</dd>
								<dt class="text-surface-500 dark:text-surface-400">Memory</dt>
								<dd class="text-surface-900 dark:text-surface-100">
									{formatBytes(detail.systemInfo.physicalMemory)}
								</dd>
								{#if detail.systemInfo.hardwareSerial}
									<dt class="text-surface-500 dark:text-surface-400">Serial</dt>
									<dd class="text-surface-900 dark:text-surface-100">
										{detail.systemInfo.hardwareSerial}
									</dd>
								{/if}
								{#if detail.systemInfo.biosVersion}
									<dt class="text-surface-500 dark:text-surface-400">BIOS</dt>
									<dd class="text-surface-900 dark:text-surface-100">
										{detail.systemInfo.biosVersion}
									</dd>
								{/if}
							{/if}
						</dl>
					</section>
				{/if}

				<!-- Package Updates -->
				{#if detail.packageUpdates}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-surface-700 dark:text-surface-300">
							Updates
						</h3>
						<div class="flex gap-4 text-sm">
							<div>
								<span class="text-lg font-semibold text-surface-900 dark:text-surface-50"
									>{detail.packageUpdates.updates.length}</span
								>
								<span class="text-surface-500 dark:text-surface-400"> total</span>
							</div>
							<div>
								<span class="text-lg font-semibold text-amber-500"
									>{detail.packageUpdates.updates.filter((u) => u.isSecurityUpdate).length}</span
								>
								<span class="text-surface-500 dark:text-surface-400"> security</span>
							</div>
						</div>
					</section>
				{/if}

				<!-- Failed Services -->
				{#if detail.failedServices.length > 0}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-red-600 dark:text-red-400">
							Failed Services ({detail.failedServices.length})
						</h3>
						<div
							class="overflow-hidden rounded-lg border border-surface-200 dark:border-surface-700"
						>
							<table class="w-full text-left text-xs">
								<thead class="bg-surface-50 dark:bg-surface-800">
									<tr>
										<th scope="col" class="px-3 py-2 font-medium">Unit</th>
										<th scope="col" class="px-3 py-2 font-medium">State</th>
									</tr>
								</thead>
								<tbody class="divide-y divide-surface-100 dark:divide-surface-800">
									{#each detail.failedServices as svc}
										<tr>
											<td
												class="px-3 py-1.5 font-mono text-surface-700 dark:text-surface-300"
												>{svc.unit}</td
											>
											<td class="px-3 py-1.5 text-red-500">{svc.subState}</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</section>
				{/if}

				<!-- Recent SSH Sessions -->
				{#if detail.recentSshSessions.length > 0}
					<section>
						<h3 class="mb-3 text-sm font-semibold text-surface-700 dark:text-surface-300">
							Recent SSH Sessions
						</h3>
						<div
							class="overflow-hidden rounded-lg border border-surface-200 dark:border-surface-700"
						>
							<table class="w-full text-left text-xs">
								<thead class="bg-surface-50 dark:bg-surface-800">
									<tr>
										<th scope="col" class="px-3 py-2 font-medium">User</th>
										<th scope="col" class="px-3 py-2 font-medium">Source</th>
										<th scope="col" class="px-3 py-2 font-medium">Action</th>
										<th scope="col" class="px-3 py-2 font-medium">Time</th>
									</tr>
								</thead>
								<tbody class="divide-y divide-surface-100 dark:divide-surface-800">
									{#each detail.recentSshSessions as sess}
										<tr class="text-surface-700 dark:text-surface-300">
											<td class="px-3 py-1.5">{sess.user}</td>
											<td class="px-3 py-1.5"
												>{sess.sourceIp || '—'}:{sess.sourcePort || '—'}</td
											>
											<td
												class="px-3 py-1.5 {sess.action === 'failed'
													? 'text-red-500'
													: ''}">{sess.action}</td
											>
											<td class="px-3 py-1.5"
												>{formatDateTime(sess.timestamp)}</td
											>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					</section>
				{/if}

				<!-- Last Updated -->
				{#if detail.telemetryLastUpdated}
					<p class="text-xs text-surface-400 dark:text-surface-500">
						Telemetry last updated: {formatRelativeTime(detail.telemetryLastUpdated)}
					</p>
				{/if}
			</div>
		{/if}
	</div>
{/if}
