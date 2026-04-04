<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { AlertRuleDto, AlertEventDto, WebhookEndpointDto, PaginatedResponse } from '$lib/api/types';
	import { Bell, AlertCircle, ChevronLeft, ChevronRight, Plus, Trash2, Check, Webhook } from 'lucide-svelte';
	import { enhance } from '$app/forms';
	import { goto } from '$app/navigation';
	import { page as pageStore } from '$app/state';
	import PageHeader from '$lib/components/PageHeader.svelte';

	let { data } = $props();

	const rules: AlertRuleDto[] | null = $derived(data.rules);
	const events: PaginatedResponse<AlertEventDto> | null = $derived(data.events);
	const webhooks: WebhookEndpointDto[] | null = $derived(data.webhooks);
	const filters = $derived(data.filters);

	let activeTab = $state<'rules' | 'events' | 'webhooks'>('rules');
	let showCreateRule = $state(false);
	let showCreateWebhook = $state(false);
	let editingRuleId = $state<number | null>(null);

	// svelte-ignore state_referenced_locally
	let statusFilter = $state(filters.status ?? '');
	// svelte-ignore state_referenced_locally
	let severityFilter = $state(filters.severity ?? '');

	const severityColors: Record<string, string> = {
		Info: 'bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400',
		Warning: 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400',
		Critical: 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400'
	};

	const statusColors: Record<string, string> = {
		Triggered: 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400',
		Acknowledged: 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400',
		Resolved: 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400'
	};

	function formatTimestamp(ts: string): string {
		return new Date(ts).toLocaleString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: '2-digit',
			minute: '2-digit'
		});
	}

	function applyEventFilters() {
		const params = new URLSearchParams();
		if (statusFilter) params.set('status', statusFilter);
		if (severityFilter) params.set('severity', severityFilter);
		params.set('page', '1');

		goto(`/settings/alerts?${params.toString()}`);
	}

	function clearEventFilters() {
		statusFilter = '';
		severityFilter = '';
		goto('/settings/alerts');
	}

	function goToPage(p: number) {
		const params = new URLSearchParams(pageStore.url.searchParams);
		params.set('page', String(p));

		goto(`/settings/alerts?${params.toString()}`);
	}
</script>

<div class="space-y-6">
	<PageHeader title="Alerts" description="Manage alert rules, view alert events, and configure webhook delivery." />

	{#if rules === null}
		<div class="flex items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 p-6 dark:border-amber-800 dark:bg-amber-900/20">
			<AlertCircle class="h-5 w-5 text-amber-600 dark:text-amber-400" />
			<p class="text-sm text-amber-700 dark:text-amber-300">
				Alerting is available on Pro and Team plans. Upgrade your subscription to access this feature.
			</p>
		</div>
	{:else}
		<!-- Tabs -->
		<div class="flex gap-1 rounded-lg border border-surface-200 bg-surface-100 p-1 dark:border-surface-700 dark:bg-surface-800">
			<button
				onclick={() => (activeTab = 'rules')}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'rules'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Alert Rules ({rules.length})
			</button>
			<button
				onclick={() => (activeTab = 'events')}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'events'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Alert Events {events ? `(${events.totalCount})` : ''}
			</button>
			<button
				onclick={() => (activeTab = 'webhooks')}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'webhooks'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Webhooks ({webhooks?.length ?? 0})
			</button>
		</div>

		<!-- Alert Rules Tab -->
		{#if activeTab === 'rules'}
			<div class="space-y-4">
				<div class="flex items-center justify-between">
					<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Alert Rules</h2>
					<button
						onclick={() => (showCreateRule = !showCreateRule)}
						class="flex items-center gap-2 rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600"
					>
						<Plus class="h-4 w-4" />
						New Rule
					</button>
				</div>

				<!-- Create Rule Form -->
				{#if showCreateRule}
					<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
						<h3 class="mb-4 text-sm font-semibold text-surface-900 dark:text-surface-50">Create Alert Rule</h3>
						<form method="POST" action="?/createRule" use:enhance={() => { return async ({ update }) => { showCreateRule = false; await update(); }; }}>
							<div class="grid grid-cols-1 gap-4 md:grid-cols-2">
								<div>
									<label for="rule-name" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Name</label>
									<input id="rule-name" name="name" type="text" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
								<div>
									<label for="rule-desc" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Description</label>
									<input id="rule-desc" name="description" type="text" class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
								<div>
									<label for="rule-metric" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Metric</label>
									<select id="rule-metric" name="metric" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
										<option value="CpuUsage">CPU Usage</option>
										<option value="MemoryUsage">Memory Usage</option>
										<option value="DiskUsage">Disk Usage</option>
										<option value="FailedServices">Failed Services</option>
										<option value="SecurityUpdates">Security Updates</option>
										<option value="DiskHealth">Disk Health</option>
									</select>
								</div>
								<div>
									<label for="rule-operator" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Operator</label>
									<select id="rule-operator" name="operator" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
										<option value="GreaterThan">Greater Than</option>
										<option value="LessThan">Less Than</option>
										<option value="Equals">Equals</option>
									</select>
								</div>
								<div>
									<label for="rule-threshold" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Threshold</label>
									<input id="rule-threshold" name="threshold" type="number" step="any" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
								<div>
									<label for="rule-duration" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Duration (minutes)</label>
									<input id="rule-duration" name="durationMinutes" type="number" value="0" class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
								<div>
									<label for="rule-severity" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Severity</label>
									<select id="rule-severity" name="severity" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
										<option value="Info">Info</option>
										<option value="Warning">Warning</option>
										<option value="Critical">Critical</option>
									</select>
								</div>
								<div class="flex items-center gap-6 pt-5">
									<label class="flex items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
										<input name="notifyEmail" type="checkbox" checked class="rounded border-surface-300" />
										Email
									</label>
									<label class="flex items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
										<input name="notifyWebhook" type="checkbox" class="rounded border-surface-300" />
										Webhook
									</label>
								</div>
							</div>
							<div class="mt-4 flex justify-end gap-2">
								<button type="button" onclick={() => (showCreateRule = false)} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Cancel</button>
								<button type="submit" class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600">Create Rule</button>
							</div>
						</form>
					</div>
				{/if}

				<!-- Rules List -->
				<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
					<div class="overflow-x-auto">
						<table class="w-full text-sm">
							<thead>
								<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Name</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Metric</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Condition</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Severity</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Notify</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
								</tr>
							</thead>
							<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
								{#if rules.length === 0}
									<tr>
										<td colspan="7" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
											<Bell class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
											No alert rules configured.
										</td>
									</tr>
								{:else}
									{#each rules as rule, i}
										<tr class="{i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-50 dark:hover:bg-surface-800/50">
											<td class="px-4 py-3">
												<div class="font-medium text-surface-900 dark:text-surface-100">{rule.name}</div>
												{#if rule.description}
													<div class="text-xs text-surface-500 dark:text-surface-400">{rule.description}</div>
												{/if}
												{#if rule.isCustom}
													<span class="text-xs text-primary-600 dark:text-primary-400">Custom</span>
												{/if}
											</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">{rule.metric}</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">
												{rule.operator} {rule.threshold}
												{#if rule.durationMinutes > 0}
													<span class="text-xs text-surface-400">for {rule.durationMinutes}m</span>
												{/if}
											</td>
											<td class="px-4 py-3">
												<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {severityColors[rule.severity] ?? ''}">
													{rule.severity}
												</span>
											</td>
											<td class="px-4 py-3">
												{#if rule.isEnabled}
													<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400">Enabled</span>
												{:else}
													<span class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-500 dark:bg-surface-700 dark:text-surface-400">Disabled</span>
												{/if}
											</td>
											<td class="px-4 py-3 text-xs text-surface-500 dark:text-surface-400">
												{#if rule.notifyEmail}Email{/if}
												{#if rule.notifyEmail && rule.notifyWebhook}, {/if}
												{#if rule.notifyWebhook}Webhook{/if}
											</td>
											<td class="px-4 py-3">
												<div class="flex items-center gap-2">
													{#if editingRuleId === rule.id}
														<form method="POST" action="?/updateRule" use:enhance={() => { return async ({ update }) => { editingRuleId = null; await update(); }; }} class="flex items-center gap-2">
															<input type="hidden" name="id" value={rule.id} />
															<input type="hidden" name="name" value={rule.name} />
															<input type="hidden" name="threshold" value={rule.threshold} />
															<input type="hidden" name="durationMinutes" value={rule.durationMinutes} />
															<input type="hidden" name="severity" value={rule.severity} />
															<label class="flex items-center gap-1 text-xs">
																<input name="isEnabled" type="checkbox" checked={rule.isEnabled} class="rounded" />
																On
															</label>
															<label class="flex items-center gap-1 text-xs">
																<input name="notifyEmail" type="checkbox" checked={rule.notifyEmail} class="rounded" />
																Email
															</label>
															<label class="flex items-center gap-1 text-xs">
																<input name="notifyWebhook" type="checkbox" checked={rule.notifyWebhook} class="rounded" />
																WH
															</label>
															<button type="submit" class="rounded p-1 text-green-600 hover:bg-green-50 dark:text-green-400 dark:hover:bg-green-900/20">
																<Check class="h-4 w-4" />
															</button>
														</form>
													{:else}
														<button onclick={() => (editingRuleId = rule.id)} class="text-xs text-primary-600 hover:underline dark:text-primary-400">Edit</button>
														{#if rule.isCustom}
															<form method="POST" action="?/deleteRule" use:enhance>
																<input type="hidden" name="id" value={rule.id} />
																<button type="submit" class="rounded p-1 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20">
																	<Trash2 class="h-4 w-4" />
																</button>
															</form>
														{/if}
													{/if}
												</div>
											</td>
										</tr>
									{/each}
								{/if}
							</tbody>
						</table>
					</div>
				</div>
			</div>
		{/if}

		<!-- Alert Events Tab -->
		{#if activeTab === 'events' && events}
			<div class="space-y-4">
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Alert Events</h2>

				<!-- Event Filters -->
				<div class="flex flex-wrap items-end gap-4 rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
					<div>
						<label for="status-filter" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Status</label>
						<select id="status-filter" bind:value={statusFilter} class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-1.5 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
							<option value="">All</option>
							<option value="Triggered">Triggered</option>
							<option value="Acknowledged">Acknowledged</option>
							<option value="Resolved">Resolved</option>
						</select>
					</div>
					<div>
						<label for="severity-filter" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Severity</label>
						<select id="severity-filter" bind:value={severityFilter} class="rounded-lg border border-surface-300 bg-surface-50 px-3 py-1.5 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
							<option value="">All</option>
							<option value="Info">Info</option>
							<option value="Warning">Warning</option>
							<option value="Critical">Critical</option>
						</select>
					</div>
					<button onclick={applyEventFilters} class="rounded-lg bg-primary-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600">Filter</button>
					<button onclick={clearEventFilters} class="rounded-lg border border-surface-300 px-4 py-1.5 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Clear</button>
				</div>

				<!-- Events Table -->
				<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
					<div class="overflow-x-auto">
						<table class="w-full text-sm">
							<thead>
								<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Triggered</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Rule</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Machine</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Severity</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Message</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
								</tr>
							</thead>
							<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
								{#if events.items.length === 0}
									<tr>
										<td colspan="7" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
											<Bell class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
											No alert events found.
										</td>
									</tr>
								{:else}
									{#each events.items as event, i}
										<tr class="{i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-50 dark:hover:bg-surface-800/50">
											<td class="whitespace-nowrap px-4 py-3 text-surface-900 dark:text-surface-100">
												{formatTimestamp(event.triggeredAt)}
											</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">{event.ruleName}</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">#{event.machineId}</td>
											<td class="px-4 py-3">
												<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {severityColors[event.severity] ?? ''}">
													{event.severity}
												</span>
											</td>
											<td class="px-4 py-3">
												<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {statusColors[event.status] ?? ''}">
													{event.status}
												</span>
											</td>
											<td class="max-w-xs truncate px-4 py-3 text-surface-600 dark:text-surface-400" title={event.message}>
												{event.message}
											</td>
											<td class="px-4 py-3">
												{#if event.status === 'Triggered'}
													<form method="POST" action="?/acknowledgeEvent" use:enhance>
														<input type="hidden" name="id" value={event.id} />
														<button type="submit" class="rounded-lg border border-surface-300 px-3 py-1 text-xs font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">
															Acknowledge
														</button>
													</form>
												{/if}
											</td>
										</tr>
									{/each}
								{/if}
							</tbody>
						</table>
					</div>

					{#if events.totalPages > 1}
						<div class="flex items-center justify-between border-t border-surface-200 px-4 py-3 dark:border-surface-700">
							<p class="text-sm text-surface-500 dark:text-surface-400">
								Showing {(events.page - 1) * events.pageSize + 1} to {Math.min(events.page * events.pageSize, events.totalCount)} of {events.totalCount}
							</p>
							<div class="flex items-center gap-2">
								<button onclick={() => goToPage(events.page - 1)} disabled={events.hasPreviousPage === false} class="rounded-lg border border-surface-300 p-1.5 text-surface-600 hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700">
									<ChevronLeft class="h-4 w-4" />
								</button>
								<span class="text-sm text-surface-600 dark:text-surface-400">Page {events.page} of {events.totalPages}</span>
								<button onclick={() => goToPage(events.page + 1)} disabled={events.hasNextPage === false} class="rounded-lg border border-surface-300 p-1.5 text-surface-600 hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-400 dark:hover:bg-surface-700">
									<ChevronRight class="h-4 w-4" />
								</button>
							</div>
						</div>
					{/if}
				</div>
			</div>
		{/if}

		<!-- Webhooks Tab -->
		{#if activeTab === 'webhooks' && webhooks}
			<div class="space-y-4">
				<div class="flex items-center justify-between">
					<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Webhook Endpoints</h2>
					<button
						onclick={() => (showCreateWebhook = !showCreateWebhook)}
						class="flex items-center gap-2 rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600"
					>
						<Plus class="h-4 w-4" />
						New Webhook
					</button>
				</div>

				{#if showCreateWebhook}
					<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
						<h3 class="mb-4 text-sm font-semibold text-surface-900 dark:text-surface-50">Create Webhook</h3>
						<form method="POST" action="?/createWebhook" use:enhance={() => { return async ({ update }) => { showCreateWebhook = false; await update(); }; }}>
							<div class="grid grid-cols-1 gap-4 md:grid-cols-2">
								<div>
									<label for="webhook-name" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Name</label>
									<input id="webhook-name" name="name" type="text" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
								<div>
									<label for="webhook-url" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">URL</label>
									<input id="webhook-url" name="url" type="url" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
								</div>
							</div>
							<div class="mt-4 flex justify-end gap-2">
								<button type="button" onclick={() => (showCreateWebhook = false)} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Cancel</button>
								<button type="submit" class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600">Create Webhook</button>
							</div>
						</form>
					</div>
				{/if}

				<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
					<div class="overflow-x-auto">
						<table class="w-full text-sm">
							<thead>
								<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Name</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">URL</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Created</th>
									<th class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
								</tr>
							</thead>
							<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
								{#if webhooks.length === 0}
									<tr>
										<td colspan="5" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
											<Webhook class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
											No webhook endpoints configured.
										</td>
									</tr>
								{:else}
									{#each webhooks as webhook, i}
										<tr class="{i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-50 dark:hover:bg-surface-800/50">
											<td class="px-4 py-3 font-medium text-surface-900 dark:text-surface-100">{webhook.name}</td>
											<td class="max-w-xs truncate px-4 py-3 text-surface-600 dark:text-surface-400" title={webhook.url}>{webhook.url}</td>
											<td class="px-4 py-3">
												{#if webhook.isEnabled}
													<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400">Active</span>
												{:else}
													<span class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-500 dark:bg-surface-700 dark:text-surface-400">Disabled</span>
												{/if}
											</td>
											<td class="whitespace-nowrap px-4 py-3 text-surface-500 dark:text-surface-400">{formatTimestamp(webhook.createdAt)}</td>
											<td class="px-4 py-3">
												<form method="POST" action="?/deleteWebhook" use:enhance>
													<input type="hidden" name="id" value={webhook.id} />
													<button type="submit" class="rounded p-1 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20">
														<Trash2 class="h-4 w-4" />
													</button>
												</form>
											</td>
										</tr>
									{/each}
								{/if}
							</tbody>
						</table>
					</div>
				</div>
			</div>
		{/if}
	{/if}
</div>
