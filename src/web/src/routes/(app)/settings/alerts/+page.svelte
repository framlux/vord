<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { AlertRuleDto, AlertEventDto, IntegrationEndpointDto, IntegrationProviderDto, IntegrationTestResultDto, PaginatedResponse } from '$lib/api/types';
	import { Bell, CircleAlert, ChevronLeft, ChevronRight, Plus, Trash2, Check, Plug, Copy, RefreshCw, Zap } from 'lucide-svelte';
	import { tick } from 'svelte';
	import { enhance } from '$app/forms';
	import { goto } from '$app/navigation';
	import { page as pageState } from '$app/state';
	import PageHeader from '$lib/components/PageHeader.svelte';
	import ConfirmDialog from '$lib/components/ConfirmDialog.svelte';
	import MachineAssignmentModal from '$lib/components/machine/MachineAssignmentModal.svelte';
	import { canAuthorAlertRules, canManageAlertRules } from '$lib/utils/alert-entitlement';
	import { formatDateTime } from '$lib/utils/format';

	let { data } = $props();

	// A self-hosted deployment has no rule quota, so the usage counter would only ever report a
	// meaningless ceiling.
	const selfHosted: boolean = $derived(data.user?.deployment?.selfHosted === true);

	const rules: AlertRuleDto[] = $derived(data.rules ?? []);
	const events: PaginatedResponse<AlertEventDto> | null = $derived(data.events);
	const integrations: IntegrationEndpointDto[] | null = $derived(data.integrations);
	const providers: IntegrationProviderDto[] | null = $derived(data.providers);
	const filters = $derived(data.filters);

	// A Free tenant still sees its built-in rules — an invisible alert rule is indistinguishable from
	// no alert rule — but every control that changes one is withheld. The predicate lives beside the
	// machine page's copy so the two cannot drift.
	const entitled: boolean = $derived(canManageAlertRules(data.subscription, selfHosted));

	// Retuning a built-in rule is authoring a rule, which is what the Team tier sells. Pro may turn a
	// built-in on or off and choose which machines it watches; below Team it may not touch anything
	// else, and it may not touch a custom rule at all.
	const canAuthorRules: boolean = $derived(canAuthorAlertRules(data.subscription, selfHosted));

	// A paid tier that has lapsed is refused the same controls as Free, but for a different reason,
	// and telling it to upgrade a plan it already pays for reads as a bug.
	const lapsedPaidTier: boolean = $derived(
		entitled === false && data.subscription !== null && data.subscription !== undefined &&
		(data.subscription.tier === 'Pro' || data.subscription.tier === 'Team')
	);

	function canModify(rule: AlertRuleDto): boolean {
		if (rule.isCustom) {
			return canAuthorRules;
		}

		return entitled;
	}

	const validTabs = ['rules', 'events', 'integrations'] as const;
	const urlTab = pageState.url.searchParams.get('tab');
	let activeTab = $state<'rules' | 'events' | 'integrations'>(
		validTabs.includes(urlTab as typeof validTabs[number]) ? (urlTab as 'rules' | 'events' | 'integrations') : 'rules'
	);
	let showCreateRule = $state(false);
	let connectingProvider = $state<string | null>(null);
	let editingRuleId = $state<number | null>(null);
	let deleteRuleConfirm = $state<{ open: boolean; id: number | null }>({ open: false, id: null });
	let deleteIntegrationConfirm = $state<{ open: boolean; id: number | null }>({ open: false, id: null });
	let revealedSecret = $state<string | null>(null);
	let secretCopied = $state(false);
	let rulesError = $state<string | null>(null);
	let eventsError = $state<string | null>(null);
	let integrationsError = $state<string | null>(null);
	let testResults = $state<Record<number, IntegrationTestResultDto>>({});
	let deleteRuleForm: HTMLFormElement;
	let deleteIntegrationForm: HTMLFormElement;

	// Metric category metadata for duration minimums and event-based metrics
	const metricMinDuration: Record<string, number> = {
		CpuUsage: 5,
		MemoryUsage: 5,
		DiskUsage: 5,
		MachineOffline: 1,
		FailedServices: 1,
		SecurityUpdates: 1,
		DiskHealth: 1,
		SshConnection: 0,
		TelemetryStale: 1,
		// Failed-login windows are evaluated on the server's five-minute cadence, so a shorter window
		// would leave most of the traffic uninspected and detect less than the built-in rule does.
		FailedSshLogin: 5
	};

	// These are two different questions that used to share one set, and conflating them hid the very
	// inputs a metric needed. "Measured from events" asks where the number comes from: events as they
	// arrive, rather than the periodic sweep over machine state. "Has nothing to configure" asks
	// whether the rule has an operator, a threshold and a window at all. An SSH connection is both —
	// it fires on the event itself, so there is no number to compare. A failed SSH login is only the
	// first: the failures are counted as they arrive, but the count is then compared against a
	// threshold over a window the tenant chooses, so every one of those inputs still applies.
	const eventMeasuredMetrics = new Set(['SshConnection', 'FailedSshLogin']);
	const metricsWithoutThreshold = new Set(['SshConnection']);

	const defaultOperators: Array<{ value: string; label: string }> = [
		{ value: 'GreaterThan', label: 'Greater Than' },
		{ value: 'LessThan', label: 'Less Than' },
		{ value: 'EqualTo', label: 'Equals' }
	];

	// The server refuses anything but a greater-than comparison on a failed-login count. Equals never
	// fires when a burst takes the count straight from four to seven, and less-than fires on almost
	// every quiet window. Offering either would only build a rule the save rejects.
	const metricOperators: Record<string, Array<{ value: string; label: string }>> = {
		FailedSshLogin: [{ value: 'GreaterThan', label: 'Greater Than' }]
	};

	// Which picker is open, if any. The three are mutually exclusive — a rule row, the create form
	// and one rule's edit form — so they share a single modal rather than mounting one each.
	type PickerTarget = { kind: 'assign'; ruleId: number } | { kind: 'create' } | { kind: 'edit'; ruleId: number };
	let picker = $state<PickerTarget | null>(null);

	// Selections live here rather than on the rendered rows, because the modal reaches the whole
	// fleet a page at a time and a checkbox list bound to one page loses everything off it.
	// "Offered" is what the save is allowed to unassign: the API carries through any assignment the
	// caller could not see, so a machine the picker represented has to be named or unticking it
	// would silently do nothing.
	let createSelectedIds = $state<number[]>([]);
	let editSelectedIds = $state<number[]>([]);
	let editOfferedIds = $state<number[]>([]);

	const pickerRule = $derived.by(() => {
		const target = picker;
		if (target === null || target.kind === 'create') {
			return null;
		}

		return rules.find((r) => r.id === target.ruleId) ?? null;
	});

	const pickerInitialIds = $derived.by(() => {
		if (picker === null) {
			return [];
		}
		if (picker.kind === 'create') {
			return createSelectedIds;
		}

		return picker.kind === 'edit' ? editSelectedIds : (pickerRule?.machineIds ?? []);
	});

	const pickerTitle = $derived(
		picker === null
			? ''
			: picker.kind === 'create'
				? 'Machines for the new rule'
				: `Machines watched by ${pickerRule?.name ?? 'this rule'}`
	);

	// Only the assignment route accepts an empty set, where it parks a rule so it watches nothing
	// without switching it off. Both validators refuse an empty array on create and update, so
	// offering a save there would build a request the server answers 400.
	const pickerAllowsEmpty = $derived(picker?.kind === 'assign');

	let assignForm: HTMLFormElement;
	let assignRuleId = $state<number | null>(null);
	let assignMachineIds = $state('');
	let assignOfferedIds = $state('');

	function machineLabel(count: number): string {
		return count === 1 ? '1 machine' : `${count} machines`;
	}

	async function onPickerSave(machineIds: number[], offeredIds: number[]) {
		const target = picker;
		if (target === null) {
			return;
		}

		if (target.kind === 'create') {
			createSelectedIds = machineIds;
			picker = null;

			return;
		}

		if (target.kind === 'edit') {
			editSelectedIds = machineIds;
			editOfferedIds = offeredIds;
			picker = null;

			return;
		}

		// Assignment saves on its own endpoint rather than through the rule update, so it posts
		// immediately instead of waiting for a surrounding form to be submitted. The rule id is held
		// apart from the picker because closing the picker must not blank the field the submit is
		// about to read.
		assignRuleId = target.ruleId;
		assignMachineIds = machineIds.join(',');
		assignOfferedIds = offeredIds.join(',');
		rulesError = null;
		picker = null;

		// These inputs are bound to state, and state reaches the DOM on the next tick. Submitting in
		// the same turn would post whatever they held before this save.
		await tick();
		assignForm.requestSubmit();
	}

	let createMetric = $state('CpuUsage');
	const createHasThreshold = $derived(metricsWithoutThreshold.has(createMetric) === false);
	const createMinDuration = $derived(metricMinDuration[createMetric] ?? 1);
	const createOperators = $derived(metricOperators[createMetric] ?? defaultOperators);
	const createIsEventMeasured = $derived(eventMeasuredMetrics.has(createMetric));

	async function copySecret() {
		if (revealedSecret) {
			try {
				await navigator.clipboard.writeText(revealedSecret);
				secretCopied = true;
				setTimeout(() => { secretCopied = false; }, 2000);
			} catch {
				integrationsError = 'Failed to copy secret to clipboard. Please select and copy it manually.';
			}
		}
	}

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

	function switchTab(tab: 'rules' | 'events' | 'integrations') {
		activeTab = tab;
		const params = new URLSearchParams(pageState.url.searchParams);
		params.set('tab', tab);
		goto(`/settings/alerts?${params.toString()}`, { replaceState: true });
	}

	function applyEventFilters() {
		const params = new URLSearchParams(pageState.url.searchParams);
		if (statusFilter) { params.set('status', statusFilter); } else { params.delete('status'); }
		if (severityFilter) { params.set('severity', severityFilter); } else { params.delete('severity'); }
		params.set('page', '1');
		params.set('tab', 'events');

		goto(`/settings/alerts?${params.toString()}`);
	}

	function clearEventFilters() {
		statusFilter = '';
		severityFilter = '';
		goto('/settings/alerts?tab=events');
	}

	function goToPage(p: number) {
		const params = new URLSearchParams(pageState.url.searchParams);
		params.set('page', String(p));

		goto(`/settings/alerts?${params.toString()}`);
	}

	const tabOrder: Array<'rules' | 'events' | 'integrations'> = ['rules', 'events', 'integrations'];

	function handleTabKeydown(event: KeyboardEvent) {
		const currentIndex = tabOrder.indexOf(activeTab);
		let newIndex = currentIndex;

		if (event.key === 'ArrowRight') {
			newIndex = (currentIndex + 1) % tabOrder.length;
		} else if (event.key === 'ArrowLeft') {
			newIndex = (currentIndex - 1 + tabOrder.length) % tabOrder.length;
		} else {
			return;
		}

		event.preventDefault();
		switchTab(tabOrder[newIndex]);

		const tabId = `tab-${tabOrder[newIndex]}`;
		const tabElement = document.getElementById(tabId);
		if (tabElement) {
			tabElement.focus();
		}
	}
</script>

<svelte:head><title>Alerts - Vord</title></svelte:head>

<div class="space-y-6">
	<PageHeader title="Alerts" description="Manage alert rules, view alert events, and configure integrations." />

	{#if entitled === false}
		<div class="flex items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 p-6 dark:border-amber-800 dark:bg-amber-900/20">
			<CircleAlert class="h-5 w-5 text-amber-600 dark:text-amber-400" />
			{#if lapsedPaidTier}
				<p class="text-sm text-amber-700 dark:text-amber-300">
					Alert rules stay switched off while your subscription is not active. Settle your <a href="/settings/billing" class="underline hover:text-amber-800 dark:hover:text-amber-200">billing</a> to turn them back on.
				</p>
			{:else}
				<p class="text-sm text-amber-700 dark:text-amber-300">
					These built-in alert rules only run on Pro and Team plans. Upgrade your subscription to turn them on and choose the machines they watch.
				</p>
			{/if}
		</div>
	{/if}

		<!-- Tabs -->
		<div role="tablist" class="flex gap-1 rounded-lg border border-surface-200 bg-surface-100 p-1 dark:border-surface-700 dark:bg-surface-800">
			<button
				id="tab-rules"
				role="tab"
				aria-selected={activeTab === 'rules'}
				aria-controls="tabpanel-rules"
				tabindex={activeTab === 'rules' ? 0 : -1}
				onclick={() => switchTab('rules')}
				onkeydown={handleTabKeydown}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'rules'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Alert Rules ({rules.length})
			</button>
			<button
				id="tab-events"
				role="tab"
				aria-selected={activeTab === 'events'}
				aria-controls="tabpanel-events"
				tabindex={activeTab === 'events' ? 0 : -1}
				onclick={() => switchTab('events')}
				onkeydown={handleTabKeydown}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'events'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Alert Events {events ? `(${events.totalCount})` : ''}
			</button>
			<button
				id="tab-integrations"
				role="tab"
				aria-selected={activeTab === 'integrations'}
				aria-controls="tabpanel-integrations"
				tabindex={activeTab === 'integrations' ? 0 : -1}
				onclick={() => switchTab('integrations')}
				onkeydown={handleTabKeydown}
				class="flex-1 rounded-md px-4 py-2 text-sm font-medium transition-colors {activeTab === 'integrations'
					? 'bg-surface-50 text-surface-900 shadow dark:bg-surface-700 dark:text-surface-100'
					: 'text-surface-500 hover:text-surface-700 dark:text-surface-400 dark:hover:text-surface-200'}"
			>
				Integrations ({integrations?.length ?? 0})
			</button>
		</div>

		<!-- Alert Rules Tab -->
		{#if activeTab === 'rules'}
			<div id="tabpanel-rules" role="tabpanel" aria-labelledby="tab-rules" class="space-y-4">
				{#if rulesError && showCreateRule === false && editingRuleId === null}
					<div role="alert" class="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
						{rulesError}
						<button onclick={() => { rulesError = null; }} class="ml-2 text-red-500 underline hover:text-red-700 dark:text-red-400 dark:hover:text-red-300">Dismiss</button>
					</div>
				{/if}
				<div class="flex items-center justify-between">
					<div class="flex items-center gap-3">
						<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Alert Rules</h2>
						{#if canAuthorRules && selfHosted === false && data.subscription?.alertRuleLimit !== null && data.subscription?.alertRuleLimit !== undefined}
							<span class="text-sm text-surface-500 dark:text-surface-400">
								{data.subscription.alertRuleCount} of {data.subscription.alertRuleLimit} rules used
							</span>
							{#if data.subscription.alertRuleCount >= data.subscription.alertRuleLimit}
								<span class="text-sm font-medium text-red-600 dark:text-red-400">Limit reached</span>
							{:else if data.subscription.alertRuleCount >= data.subscription.alertRuleLimit * 0.8}
								<span class="text-sm font-medium text-amber-600 dark:text-amber-400">Approaching limit</span>
							{/if}
						{/if}
					</div>
					<!-- Writing a rule of your own is what the Team tier sells, so the quota and the button
					     that spends it belong to Team. Pro and Free are here for their built-in rules. -->
					{#if canAuthorRules}
						<button
							onclick={() => { showCreateRule = !showCreateRule; rulesError = null; }}
							disabled={selfHosted === false && data.subscription?.alertRuleLimit !== null && data.subscription?.alertRuleLimit !== undefined && data.subscription.alertRuleCount >= data.subscription.alertRuleLimit}
							class="flex items-center gap-2 rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-primary-700 disabled:cursor-not-allowed disabled:opacity-50 dark:bg-primary-500 dark:hover:bg-primary-600"
						>
							<Plus class="h-4 w-4" />
							New Rule
						</button>
					{/if}
				</div>

				<!-- Create Rule Form -->
				{#if showCreateRule}
					<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
						<h3 class="mb-4 text-sm font-semibold text-surface-900 dark:text-surface-50">Create Alert Rule</h3>
						<form method="POST" action="?/createRule" use:enhance={() => { rulesError = null; return async ({ result, update }) => { if (result.type === 'failure') { rulesError = (result.data as { message?: string })?.message ?? 'An error occurred'; } else { showCreateRule = false; rulesError = null; await update(); } }; }}>
							{#if rulesError}
								<div role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
									{rulesError}
								</div>
							{/if}
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
									<select id="rule-metric" name="metric" required bind:value={createMetric} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
										<option value="CpuUsage">CPU Usage</option>
										<option value="MemoryUsage">Memory Usage</option>
										<option value="DiskUsage">Disk Usage</option>
										<option value="FailedServices">Failed Services</option>
										<option value="SecurityUpdates">Security Updates</option>
										<option value="DiskHealth">Disk Health</option>
										<option value="MachineOffline">Machine Offline</option>
										<option value="SshConnection">SSH Connection</option>
										<option value="FailedSshLogin">Failed SSH Logins</option>
										<option value="TelemetryStale">Telemetry Stopped</option>
									</select>
								</div>
								{#if createHasThreshold}
									<div>
										<label for="rule-operator" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Operator</label>
										<!-- Remounting on a metric change drops any selection the new metric does not allow,
										     so the form can never post an operator that is no longer on offer. -->
										{#key createMetric}
											<select id="rule-operator" name="operator" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
												{#each createOperators as operator}
													<option value={operator.value}>{operator.label}</option>
												{/each}
											</select>
										{/key}
										{#if createOperators.length < defaultOperators.length}
											<p class="mt-1 text-xs text-surface-400 dark:text-surface-500">This metric counts events in a window, so only a greater-than comparison is meaningful.</p>
										{/if}
									</div>
									<div>
										<label for="rule-threshold" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Threshold</label>
										<input id="rule-threshold" name="threshold" type="number" step="any" required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
									</div>
									<div>
										<label for="rule-duration" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Duration (minutes)</label>
										<input id="rule-duration" name="durationMinutes" type="number" value={createMinDuration} min={createMinDuration} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
										{#if createIsEventMeasured}
											<p class="mt-1 text-xs text-surface-400 dark:text-surface-500">Minimum {createMinDuration} min. Events are counted over this window and the alert fires when the window closes.</p>
										{:else}
											<p class="mt-1 text-xs text-surface-400 dark:text-surface-500">Minimum {createMinDuration} min. Condition must be sustained before the alert fires.</p>
										{/if}
									</div>
								{:else}
									<input type="hidden" name="operator" value="EqualTo" />
									<input type="hidden" name="threshold" value="1" />
									<input type="hidden" name="durationMinutes" value="0" />
									<div class="col-span-2">
										<p class="pt-5 text-xs text-surface-500 dark:text-surface-400">This event-based alert fires immediately when the event is detected. No threshold or duration applies.</p>
									</div>
								{/if}
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
							<div class="mt-4">
								<span class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Machines (at least 1 required)</span>
								<!-- The picker reaches the whole fleet rather than the first page of it, so the
								     selection travels as one field instead of a checkbox per machine. -->
								<input type="hidden" name="machineIds" value={createSelectedIds.join(',')} />
								<div class="flex items-center gap-3">
									<button
										type="button"
										onclick={() => { picker = { kind: 'create' }; rulesError = null; }}
										class="rounded-lg border border-surface-300 px-3 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
									>
										Choose machines
									</button>
									<span class="text-sm text-surface-600 dark:text-surface-400">
										{#if createSelectedIds.length === 0}
											No machines chosen yet
										{:else}
											{machineLabel(createSelectedIds.length)} chosen
										{/if}
									</span>
								</div>
							</div>
							<div class="mt-4 flex justify-end gap-2">
								<button type="button" onclick={() => { showCreateRule = false; rulesError = null; }} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Cancel</button>
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
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Name</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Metric</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Condition</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Severity</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Machines</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Notify</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
								</tr>
							</thead>
							<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
								{#if rules.length === 0}
									<tr>
										<td colspan="8" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
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
												{:else}
													<span class="text-xs text-surface-500 dark:text-surface-400">Built-in</span>
												{/if}
											</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">{rule.metric}</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">
												{#if metricsWithoutThreshold.has(rule.metric)}
													<span class="text-xs text-surface-400">On event</span>
												{:else}
													{rule.operator} {rule.threshold}
													{#if rule.durationMinutes > 0}
														<span class="text-xs text-surface-400">for {rule.durationMinutes}m</span>
													{/if}
												{/if}
											</td>
											<td class="px-4 py-3">
												<span class="inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {severityColors[rule.severity] ?? ''}">
													{rule.severity}
												</span>
											</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">
												<!-- A rule with no machines assigned watches nothing, and rules arrive unassigned,
												     so silence here is indistinguishable from an alert that will never fire. The
												     way out of that state sits in the cell that reports it. -->
												{#if (rule.machineIds?.length ?? 0) === 0}
													<span class="text-amber-600 dark:text-amber-400">Not watching any machines yet</span>
												{:else}
													{rule.machineIds.length} machine{rule.machineIds.length === 1 ? '' : 's'}
												{/if}
												{#if canModify(rule)}
													<button
														type="button"
														aria-label="Assign machines to {rule.name}"
														onclick={() => { picker = { kind: 'assign', ruleId: rule.id }; editingRuleId = null; rulesError = null; }}
														class="mt-1 block whitespace-nowrap text-xs text-primary-600 hover:underline dark:text-primary-400"
													>
														{(rule.machineIds?.length ?? 0) === 0 ? 'Assign machines' : 'Change machines'}
													</button>
												{/if}
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
												<div class="flex items-center gap-2 whitespace-nowrap">
													{#if editingRuleId !== rule.id}
														{#if canModify(rule) && (rule.isCustom || canAuthorRules)}
															<!-- The edit form posts the selection as one field, so it has to open holding
															     what the rule already watches. Left empty it would post nothing and the
															     save would be refused for naming no machines. -->
															<button onclick={() => { editingRuleId = rule.id; editSelectedIds = [...(rule.machineIds ?? [])]; editOfferedIds = [...(rule.machineIds ?? [])]; rulesError = null; }} class="text-xs text-primary-600 hover:underline dark:text-primary-400">Edit</button>
														{/if}
														{#if canModify(rule)}
															<!-- The one change Pro may make to a built-in rule, on the endpoint that
															     accepts it alone rather than through the full-rule update. -->
															<form method="POST" action="?/toggleRule" use:enhance={() => { rulesError = null; return async ({ result, update }) => { if (result.type === 'failure') { rulesError = (result.data as { message?: string })?.message ?? 'Failed to update rule'; } else { rulesError = null; await update(); } }; }}>
																<input type="hidden" name="id" value={rule.id} />
																<input type="hidden" name="isEnabled" value={rule.isEnabled ? 'off' : 'on'} />
																<button type="submit" aria-label="{rule.isEnabled ? 'Disable' : 'Enable'} {rule.name}" class="text-xs text-primary-600 hover:underline dark:text-primary-400">
																	{rule.isEnabled ? 'Disable' : 'Enable'}
																</button>
															</form>
														{/if}
														{#if rule.isCustom && canAuthorRules}
															<button type="button" aria-label="Delete rule" onclick={() => deleteRuleConfirm = { open: true, id: rule.id }} class="rounded p-1 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20">
																<Trash2 class="h-4 w-4" />
															</button>
														{/if}
													{/if}
												</div>
											</td>
										</tr>
										{#if editingRuleId === rule.id}
											{@const editHasThreshold = metricsWithoutThreshold.has(rule.metric) === false}
											{@const editMinDuration = metricMinDuration[rule.metric] ?? 1}
											<tr class="bg-surface-50 dark:bg-surface-800/50">
												<td colspan="8" class="px-4 py-4">
													<form method="POST" action="?/updateRule" use:enhance={() => { rulesError = null; return async ({ result, update }) => { if (result.type === 'failure') { rulesError = (result.data as { message?: string })?.message ?? 'An error occurred'; } else { editingRuleId = null; rulesError = null; await update(); } }; }}>
														<input type="hidden" name="id" value={rule.id} />
														<input type="hidden" name="metric" value={rule.metric} />
														{#if rulesError}
															<div role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
																{rulesError}
															</div>
														{/if}
														<div class="grid grid-cols-1 gap-4 md:grid-cols-3 lg:grid-cols-4">
															<div>
																<label for="edit-name-{rule.id}" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Name</label>
																<input id="edit-name-{rule.id}" name="name" type="text" value={rule.name} required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
															</div>
															{#if editHasThreshold}
																<div>
																	<label for="edit-threshold-{rule.id}" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Threshold</label>
																	<input id="edit-threshold-{rule.id}" name="threshold" type="number" step="any" value={rule.threshold} required class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
																</div>
																<div>
																	<label for="edit-duration-{rule.id}" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Duration (minutes)</label>
																	<input id="edit-duration-{rule.id}" name="durationMinutes" type="number" value={rule.durationMinutes} min={editMinDuration} class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
																	<p class="mt-1 text-xs text-surface-400 dark:text-surface-500">Minimum {editMinDuration} min{eventMeasuredMetrics.has(rule.metric) ? ' — events are counted over this window' : ''}</p>
																</div>
															{:else}
																<input type="hidden" name="threshold" value={rule.threshold} />
																<input type="hidden" name="durationMinutes" value="0" />
																<div>
																	<p class="pt-5 text-xs text-surface-500 dark:text-surface-400">Event-based alert — fires immediately on detection.</p>
																</div>
															{/if}
															<div>
																<label for="edit-severity-{rule.id}" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Severity</label>
																<select id="edit-severity-{rule.id}" name="severity" class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100">
																	<option value="Info" selected={rule.severity === 'Info'}>Info</option>
																	<option value="Warning" selected={rule.severity === 'Warning'}>Warning</option>
																	<option value="Critical" selected={rule.severity === 'Critical'}>Critical</option>
																</select>
															</div>
														</div>
														<div class="mt-3 flex flex-wrap items-center gap-6">
															<label class="flex items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
																<input name="isEnabled" type="checkbox" checked={rule.isEnabled} class="rounded border-surface-300" />
																Enabled
															</label>
															<label class="flex items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
																<input name="notifyEmail" type="checkbox" checked={rule.notifyEmail} class="rounded border-surface-300" />
																Email
															</label>
															<label class="flex items-center gap-2 text-sm text-surface-700 dark:text-surface-300">
																<input name="notifyWebhook" type="checkbox" checked={rule.notifyWebhook} class="rounded border-surface-300" />
																Webhook
															</label>
														</div>
														<div class="mt-4">
															<span class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Machines (at least 1 required)</span>
															<!-- Both the selection and the set it was chosen from travel with the save. The
															     second is what bounds a removal: an assignment the caller never saw is
															     carried through rather than read as having been unticked. -->
															<input type="hidden" name="machineIds" value={editSelectedIds.join(',')} />
															<input type="hidden" name="visibleMachineIds" value={editOfferedIds.join(',')} />
															<div class="flex items-center gap-3">
																<button
																	type="button"
																	onclick={() => { picker = { kind: 'edit', ruleId: rule.id }; rulesError = null; }}
																	class="rounded-lg border border-surface-300 px-3 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
																>
																	Choose machines
																</button>
																<span class="text-sm text-surface-600 dark:text-surface-400">
																	{#if editSelectedIds.length === 0}
																		No machines chosen
																	{:else}
																		{machineLabel(editSelectedIds.length)} chosen
																	{/if}
																</span>
															</div>
														</div>
														<div class="mt-4 flex justify-end gap-2">
															<button type="button" onclick={() => { editingRuleId = null; rulesError = null; }} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Cancel</button>
															<button type="submit" class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600">Save Changes</button>
														</div>
													</form>
												</td>
											</tr>
										{/if}
									{/each}
								{/if}
							</tbody>
						</table>
					</div>
				</div>
			</div>
		{/if}

		<!-- Alert Events Tab -->
		{#if activeTab === 'events'}
			<div id="tabpanel-events" role="tabpanel" aria-labelledby="tab-events" class="space-y-4">
			{#if events}
				{#if eventsError}
					<div role="alert" class="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
						{eventsError}
						<button onclick={() => { eventsError = null; }} class="ml-2 text-red-500 underline hover:text-red-700 dark:text-red-400 dark:hover:text-red-300">Dismiss</button>
					</div>
				{/if}
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
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Triggered</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Rule</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Machine</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Severity</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Message</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
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
												{formatDateTime(event.triggeredAt)}
											</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">{event.ruleName}</td>
											<td class="px-4 py-3 text-surface-600 dark:text-surface-400">{event.machineName}</td>
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
													<form method="POST" action="?/acknowledgeEvent" use:enhance={() => { return async ({ result, update }) => { if (result.type === 'failure') { eventsError = (result.data as { message?: string })?.message ?? 'Failed to acknowledge event'; } else { eventsError = null; } await update(); }; }}>
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
			{:else}
				<div class="flex items-center gap-3 rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
					<Bell class="h-5 w-5 text-surface-400" />
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Alert events are not available. This may be due to subscription restrictions.
					</p>
				</div>
			{/if}
			</div>
		{/if}

		<!-- Integrations Tab -->
		{#if activeTab === 'integrations'}
			<div id="tabpanel-integrations" role="tabpanel" aria-labelledby="tab-integrations" class="space-y-4">
			{#if integrations}
				{#if integrationsError && connectingProvider === null}
					<div role="alert" class="rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
						{integrationsError}
						<button onclick={() => { integrationsError = null; }} class="ml-2 text-red-500 underline hover:text-red-700 dark:text-red-400 dark:hover:text-red-300">Dismiss</button>
					</div>
				{/if}

				<!-- Usage counter -->
				<div class="flex items-center justify-between">
					<div class="flex items-center gap-3">
						<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Integrations</h2>
						{#if data.subscription?.webhookLimit !== null && data.subscription?.webhookLimit !== undefined}
							<span class="text-sm text-surface-500 dark:text-surface-400">
								Using {data.subscription.webhookCount} of {data.subscription.webhookLimit} integrations
							</span>
							{#if data.subscription.webhookCount >= data.subscription.webhookLimit}
								<span class="text-sm font-medium text-red-600 dark:text-red-400">Limit reached</span>
							{:else if data.subscription.webhookCount >= data.subscription.webhookLimit * 0.8}
								<span class="text-sm font-medium text-amber-600 dark:text-amber-400">Approaching limit</span>
							{/if}
						{/if}
					</div>
				</div>

				<!-- Quick Connect cards -->
				{#if providers && providers.length > 0}
					<div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
						{#each providers as provider}
							{@const isConnected = integrations.some((i) => i.provider === provider.provider)}
							<div class="flex items-center gap-4 rounded-xl border border-surface-200 bg-surface-50 p-4 dark:border-surface-700 dark:bg-surface-800">
								<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary-100 text-lg font-bold text-primary-700 dark:bg-primary-900/30 dark:text-primary-400">
									{provider.displayName.charAt(0)}
								</div>
								<div class="flex-1 min-w-0">
									<p class="text-sm font-medium text-surface-900 dark:text-surface-100">{provider.displayName}</p>
									<p class="truncate text-xs text-surface-500 dark:text-surface-400">{provider.description}</p>
								</div>
								{#if isConnected}
									<span class="inline-flex items-center gap-1 rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400">
										<Check class="h-3 w-3" />
										Connected
									</span>
								{:else}
									<button
										onclick={() => { connectingProvider = provider.provider; integrationsError = null; }}
										disabled={data.subscription?.webhookLimit !== null && data.subscription?.webhookLimit !== undefined && data.subscription.webhookCount >= data.subscription.webhookLimit}
										class="rounded-lg bg-primary-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-primary-700 disabled:cursor-not-allowed disabled:opacity-50 dark:bg-primary-500 dark:hover:bg-primary-600"
									>
										Connect
									</button>
								{/if}
							</div>
						{/each}
					</div>
				{/if}

				<!-- Inline connect form -->
				{#if connectingProvider}
					{@const selectedProvider = providers?.find((p) => p.provider === connectingProvider)}
					{#if selectedProvider}
						<div class="rounded-xl border border-primary-200 bg-primary-50/50 p-6 dark:border-primary-800 dark:bg-primary-900/10">
							<h3 class="mb-4 text-sm font-semibold text-surface-900 dark:text-surface-50">Connect {selectedProvider.displayName}</h3>
							<form method="POST" action="?/createIntegration" use:enhance={() => { integrationsError = null; return async ({ result, update }) => { if (result.type === 'failure') { integrationsError = (result.data as { message?: string })?.message ?? 'An error occurred'; } else { if (result.type === 'success') { const resultData = result.data as { secret?: string } | null; if (resultData?.secret) { revealedSecret = resultData.secret; secretCopied = false; } } connectingProvider = null; integrationsError = null; await update(); } }; }}>
								<input type="hidden" name="provider" value={selectedProvider.provider} />
								{#if integrationsError}
									<div role="alert" class="mb-4 rounded-lg border border-red-200 bg-red-50 p-3 text-sm text-red-700 dark:border-red-800 dark:bg-red-900/20 dark:text-red-300">
										{integrationsError}
									</div>
								{/if}
								<div class="grid grid-cols-1 gap-4 md:grid-cols-2">
									<div>
										<label for="integration-name" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">Name</label>
										<input id="integration-name" name="name" type="text" value="{selectedProvider.displayName}" class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100" />
									</div>
									{#each selectedProvider.configFields as field}
										<div>
											<label for="config-{field.key}" class="mb-1 block text-xs text-surface-500 dark:text-surface-400">{field.label}</label>
											<input
												id="config-{field.key}"
												name="config.{field.key}"
												type={field.type === 'url' ? 'url' : 'text'}
												placeholder={field.placeholder}
												required
												class="w-full rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
											/>
											{#if field.helpText}
												<p class="mt-1 text-xs text-surface-400 dark:text-surface-500">
													{field.helpText}
													{#if field.helpUrl}
														<a href={field.helpUrl} target="_blank" rel="noopener noreferrer" class="text-primary-600 hover:underline dark:text-primary-400">Learn more</a>
													{/if}
												</p>
											{/if}
										</div>
									{/each}
								</div>
								<div class="mt-4 flex justify-end gap-2">
									<button type="button" onclick={() => { connectingProvider = null; integrationsError = null; }} class="rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">Cancel</button>
									<button type="submit" class="rounded-lg bg-primary-600 px-4 py-2 text-sm font-medium text-white hover:bg-primary-700 dark:bg-primary-500 dark:hover:bg-primary-600">Connect</button>
								</div>
							</form>
						</div>
					{/if}
				{/if}

				<!-- Secret revealed banner -->
				{#if revealedSecret}
					<div class="rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/20">
						<p class="mb-2 text-sm font-semibold text-amber-700 dark:text-amber-300">Integration signing secret (shown once -- copy it now):</p>
						<div class="flex items-center gap-2">
							<code class="flex-1 rounded-lg border border-amber-300 bg-white px-3 py-2 font-mono text-sm text-surface-900 dark:border-amber-700 dark:bg-surface-800 dark:text-surface-100">{revealedSecret}</code>
							<button
								onclick={copySecret}
								class="flex items-center gap-1 rounded-lg border border-amber-300 px-3 py-2 text-sm font-medium text-amber-700 hover:bg-amber-100 dark:border-amber-700 dark:text-amber-300 dark:hover:bg-amber-900/30"
							>
								<Copy class="h-4 w-4" />
								{secretCopied ? 'Copied' : 'Copy'}
							</button>
							<button
								onclick={() => { revealedSecret = null; secretCopied = false; }}
								class="rounded-lg border border-surface-300 px-3 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
							>
								Dismiss
							</button>
						</div>
						<p class="mt-2 text-xs text-amber-600 dark:text-amber-400">This secret will not be shown again. Store it securely for signature verification.</p>
					</div>
				{/if}

				<!-- Active Integrations list -->
				<div class="overflow-hidden rounded-xl border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
					<div class="overflow-x-auto">
						<table class="w-full text-sm">
							<thead>
								<tr class="border-b border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800/50">
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Provider</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Name</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Status</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Created</th>
									<th scope="col" class="px-4 py-3 text-left font-medium text-surface-500 dark:text-surface-400">Actions</th>
								</tr>
							</thead>
							<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
								{#if integrations.length === 0}
									<tr>
										<td colspan="5" class="px-4 py-8 text-center text-surface-500 dark:text-surface-400">
											<Plug class="mx-auto mb-2 h-8 w-8 text-surface-400 dark:text-surface-600" />
											No integrations configured. Connect a provider above to get started.
										</td>
									</tr>
								{:else}
									{#each integrations as integration, i}
										<tr class="{i % 2 === 1 ? 'bg-surface-100/50 dark:bg-surface-800/30' : ''} hover:bg-surface-50 dark:hover:bg-surface-800/50">
											<td class="px-4 py-3">
												<span class="inline-flex items-center gap-2 text-surface-900 dark:text-surface-100">
													<span class="flex h-6 w-6 items-center justify-center rounded bg-primary-100 text-xs font-bold text-primary-700 dark:bg-primary-900/30 dark:text-primary-400">{integration.provider.charAt(0).toUpperCase()}</span>
													{integration.provider}
												</span>
											</td>
											<td class="px-4 py-3 font-medium text-surface-900 dark:text-surface-100">{integration.name}</td>
											<td class="px-4 py-3">
												<form method="POST" action="?/updateIntegration" use:enhance={() => { return async ({ result, update }) => { if (result.type === 'failure') { integrationsError = (result.data as { message?: string })?.message ?? 'Failed to update integration'; } else { integrationsError = null; await update(); } }; }}>
													<input type="hidden" name="id" value={integration.id} />
													<input type="hidden" name="name" value={integration.name} />
													<input type="hidden" name="isEnabled" value={integration.isEnabled ? 'off' : 'on'} />
													<button type="submit" title={integration.isEnabled ? 'Disable integration' : 'Enable integration'}>
														{#if integration.isEnabled}
															<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-700 hover:bg-green-200 dark:bg-green-900/30 dark:text-green-400 dark:hover:bg-green-900/50">Active</span>
														{:else}
															<span class="inline-flex items-center rounded-full bg-surface-100 px-2.5 py-0.5 text-xs font-medium text-surface-500 hover:bg-surface-200 dark:bg-surface-700 dark:text-surface-400 dark:hover:bg-surface-600">Disabled</span>
														{/if}
													</button>
												</form>
											</td>
											<td class="whitespace-nowrap px-4 py-3 text-surface-500 dark:text-surface-400">{formatDateTime(integration.createdAt)}</td>
											<td class="px-4 py-3">
												<div class="flex items-center gap-1">
													<!-- Test button -->
													<form method="POST" action="?/testIntegration" use:enhance={() => { return async ({ result, update }) => { if (result.type === 'success') { const resultData = result.data as { testResult?: IntegrationTestResultDto } | null; if (resultData?.testResult) { testResults = { ...testResults, [integration.id]: resultData.testResult }; } } else if (result.type === 'failure') { integrationsError = (result.data as { message?: string })?.message ?? 'Failed to test integration'; } await update(); }; }}>
														<input type="hidden" name="id" value={integration.id} />
														<button type="submit" title="Test integration" aria-label="Test integration" class="rounded p-1 text-surface-500 hover:bg-surface-100 dark:text-surface-400 dark:hover:bg-surface-700">
															<Zap class="h-4 w-4" />
														</button>
													</form>
													<!-- Rotate secret (only for Custom provider) -->
													{#if integration.provider === 'Custom'}
														<form method="POST" action="?/rotateSecret" use:enhance={() => { return async ({ result, update }) => { if (result.type === 'success') { const resultData = result.data as { secret?: string } | null; if (resultData?.secret) { revealedSecret = resultData.secret; secretCopied = false; } } else if (result.type === 'failure') { integrationsError = (result.data as { message?: string })?.message ?? 'Failed to rotate secret'; } await update(); }; }}>
															<input type="hidden" name="id" value={integration.id} />
															<button type="submit" title="Rotate secret" aria-label="Rotate secret" class="rounded p-1 text-surface-500 hover:bg-surface-100 dark:text-surface-400 dark:hover:bg-surface-700">
																<RefreshCw class="h-4 w-4" />
															</button>
														</form>
													{/if}
													<!-- Delete button -->
													<button type="button" aria-label="Delete integration" onclick={() => deleteIntegrationConfirm = { open: true, id: integration.id }} class="rounded p-1 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/20">
														<Trash2 class="h-4 w-4" />
													</button>
												</div>
											</td>
										</tr>
										<!-- Test result inline -->
										{#if testResults[integration.id]}
											<tr class="bg-surface-50 dark:bg-surface-800/50">
												<td colspan="5" class="px-4 py-2">
													{#if testResults[integration.id].success}
														<div class="flex items-center gap-2 text-sm text-green-700 dark:text-green-400">
															<Check class="h-4 w-4" />
															<span>Test successful{testResults[integration.id].statusCode !== null ? ` (HTTP ${testResults[integration.id].statusCode})` : ''}: {testResults[integration.id].message}</span>
														</div>
													{:else}
														<div class="flex items-center gap-2 text-sm text-red-700 dark:text-red-400">
															<CircleAlert class="h-4 w-4" />
															<span>Test failed{testResults[integration.id].statusCode !== null ? ` (HTTP ${testResults[integration.id].statusCode})` : ''}: {testResults[integration.id].message}</span>
														</div>
													{/if}
												</td>
											</tr>
										{/if}
									{/each}
								{/if}
							</tbody>
						</table>
					</div>
				</div>

				<!-- Add Custom Webhook button -->
				<div class="flex justify-start">
					<button
						onclick={() => { connectingProvider = 'Custom'; integrationsError = null; }}
						disabled={data.subscription?.webhookLimit !== null && data.subscription?.webhookLimit !== undefined && data.subscription.webhookCount >= data.subscription.webhookLimit}
						class="flex items-center gap-2 rounded-lg border border-surface-300 px-4 py-2 text-sm font-medium text-surface-700 hover:bg-surface-100 disabled:cursor-not-allowed disabled:opacity-50 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700"
					>
						<Plus class="h-4 w-4" />
						Add Custom Webhook
					</button>
				</div>
			{:else}
				<div class="flex items-center gap-3 rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
					<Plug class="h-5 w-5 text-surface-400" />
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Integrations are not available. This may be due to subscription restrictions.
					</p>
				</div>
			{/if}
			</div>
		{/if}
</div>

<form
	method="POST"
	action="?/deleteRule"
	use:enhance={() => {
		return async ({ result, update }) => {
			deleteRuleConfirm = { open: false, id: null };
			if (result.type === 'failure') {
				rulesError = (result.data as { message?: string })?.message ?? 'Failed to delete rule';
			} else {
				rulesError = null;
				await update();
			}
		};
	}}
	bind:this={deleteRuleForm}
	class="hidden"
>
	<input type="hidden" name="id" value={deleteRuleConfirm.id ?? ''} />
</form>

<form
	method="POST"
	action="?/deleteIntegration"
	use:enhance={() => {
		return async ({ result, update }) => {
			deleteIntegrationConfirm = { open: false, id: null };
			if (result.type === 'failure') {
				integrationsError = (result.data as { message?: string })?.message ?? 'Failed to delete integration';
			} else {
				integrationsError = null;
				await update();
			}
		};
	}}
	bind:this={deleteIntegrationForm}
	class="hidden"
>
	<input type="hidden" name="id" value={deleteIntegrationConfirm.id ?? ''} />
</form>

<!-- Assignment has its own endpoint and saves on its own, so the picker posts through this rather
     than through a form wrapped around the rules table. -->
<form
	method="POST"
	action="?/assignRuleMachines"
	use:enhance={() => {
		return async ({ result, update }) => {
			if (result.type === 'failure') {
				rulesError = (result.data as { message?: string })?.message ?? 'Failed to update machines';
			} else {
				rulesError = null;
				await update();
			}
		};
	}}
	bind:this={assignForm}
	class="hidden"
>
	<input type="hidden" name="id" value={assignRuleId ?? ''} />
	<input type="hidden" name="machineIds" value={assignMachineIds} />
	<input type="hidden" name="visibleMachineIds" value={assignOfferedIds} />
</form>

<MachineAssignmentModal
	open={picker !== null}
	title={pickerTitle}
	initialSelectedIds={pickerInitialIds}
	allowEmpty={pickerAllowsEmpty}
	onsave={onPickerSave}
	oncancel={() => { picker = null; }}
/>

<ConfirmDialog
	open={deleteRuleConfirm.open}
	title="Delete Alert Rule"
	message="Are you sure you want to delete this alert rule? This cannot be undone."
	confirmLabel="Delete"
	variant="danger"
	onconfirm={() => {
		if (deleteRuleConfirm.id !== null) {
			deleteRuleForm.requestSubmit();
		}
	}}
	oncancel={() => deleteRuleConfirm = { open: false, id: null }}
/>

<ConfirmDialog
	open={deleteIntegrationConfirm.open}
	title="Delete Integration"
	message="Are you sure you want to delete this integration? This cannot be undone."
	confirmLabel="Delete"
	variant="danger"
	onconfirm={() => {
		if (deleteIntegrationConfirm.id !== null) {
			deleteIntegrationForm.requestSubmit();
		}
	}}
	oncancel={() => deleteIntegrationConfirm = { open: false, id: null }}
/>
