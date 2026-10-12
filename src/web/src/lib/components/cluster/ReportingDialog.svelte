<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import {
		commandVerb,
		describeSkip,
		OFFLINE_HOST_NOTE,
		type CommandResult,
		type ReportingPlan
	} from '$lib/utils/clusterReporting';
	import { moveFocusInto, trapTabKey } from '$lib/utils/focus-trap';
	import CommandResults from './CommandResults.svelte';

	let {
		title,
		paragraphs,
		plan,
		hasKeys,
		phase,
		results,
		confirmLabel,
		blocked,
		onconfirm,
		onclose
	}: {
		title: string;
		paragraphs: string[];
		plan: ReportingPlan;
		/** Whether this browser holds a signing key the server knows. */
		hasKeys: boolean;
		phase: 'confirm' | 'sending' | 'done';
		results: CommandResult[];
		confirmLabel: string;
		/** Why confirming is refused even though commands are planned, or null. */
		blocked: string | null;
		onconfirm: () => void;
		onclose: () => void;
	} = $props();

	// With no key at all every machine would be unauthorised, which the alert about the missing key
	// already says once.
	const unauthorised = $derived(hasKeys ? plan.skipped.filter((s) => s.reason === 'key_not_authorised') : []);
	const notSent = $derived(plan.skipped.filter((s) => s.reason !== 'key_not_authorised'));
	const canConfirm = $derived(hasKeys && plan.commands.length > 0 && blocked === null);

	let dialogElement: HTMLDivElement | undefined = $state(undefined);
	let previouslyFocused: HTMLElement | null = null;

	// The dialog is mounted only while it is open, so mounting is opening.
	$effect(() => {
		previouslyFocused = document.activeElement as HTMLElement;
		requestAnimationFrame(() => {
			moveFocusInto(dialogElement);
		});

		return () => {
			previouslyFocused?.focus();
			previouslyFocused = null;
		};
	});

	function trapFocus(event: KeyboardEvent) {
		if (dialogElement !== undefined) {
			trapTabKey(dialogElement, event);
		}
	}
</script>

<svelte:window
	onkeydown={(e) => {
		if (e.key === 'Escape' && phase !== 'sending') {
			onclose();
		}
	}}
/>

<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" role="presentation">
	<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
	<div
		bind:this={dialogElement}
		onkeydown={trapFocus}
		tabindex="-1"
		role="dialog"
		aria-modal="true"
		aria-labelledby="reporting-dialog-title"
		class="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl bg-surface-50 p-6 shadow-xl dark:bg-surface-800"
	>
		<h3 id="reporting-dialog-title" class="text-lg font-semibold text-surface-900 dark:text-surface-50">
			{title}
		</h3>

		<div class="mt-3 space-y-3 text-sm text-surface-600 dark:text-surface-400">
			{#each paragraphs as paragraph (paragraph)}
				<p>{paragraph}</p>
			{/each}
		</div>

		{#if hasKeys === false}
			<p
				role="alert"
				class="mt-4 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300"
			>
				No signing key from this browser is registered with the server.
				<a href="/settings/signing-keys" class="underline">Generate a signing key</a> first.
			</p>
		{/if}

		{#if phase === 'done'}
			<div class="mt-4">
				<h4 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
					Results
				</h4>
				<CommandResults {results} />
			</div>
		{:else if plan.commands.length > 0}
			<div class="mt-4">
				<h4 class="text-xs font-semibold uppercase tracking-wider text-surface-500 dark:text-surface-400">
					Commands to send
				</h4>
				<ul data-testid="planned-commands" class="mt-1 divide-y divide-surface-100 text-sm dark:divide-surface-700">
					{#each plan.commands as planned (planned.machineId + planned.commandType)}
						<li class="flex flex-wrap items-baseline gap-x-3 gap-y-1 py-2">
							<span class="font-semibold text-surface-900 dark:text-surface-100">{planned.machineName}</span>
							<span class="text-xs uppercase tracking-wider text-surface-500 dark:text-surface-400">
								{commandVerb(planned.commandType)}
							</span>
							{#if planned.online === false}
								<span class="text-xs text-amber-700 dark:text-amber-400">
									{OFFLINE_HOST_NOTE}
								</span>
							{/if}
						</li>
					{/each}
				</ul>
			</div>
		{/if}

		{#if unauthorised.length > 0}
			<div data-testid="unauthorised-machines" class="mt-4">
				<h4 class="text-sm font-medium text-amber-800 dark:text-amber-300">
					Your signing key is not authorised on these machines
				</h4>
				<ul class="mt-1 list-disc pl-5 text-sm text-surface-700 dark:text-surface-300">
					{#each unauthorised as skipped (skipped.machineId)}
						<li>{skipped.machineName}</li>
					{/each}
				</ul>
			</div>
		{/if}

		{#if notSent.length > 0}
			<div data-testid="skipped-machines" class="mt-4">
				<h4 class="text-sm font-medium text-amber-800 dark:text-amber-300">Not sent a command</h4>
				<ul class="mt-1 list-disc pl-5 text-sm text-surface-700 dark:text-surface-300">
					{#each notSent as skipped (skipped.machineId)}
						<li>
							<span class="font-medium">{skipped.machineName}</span>: {describeSkip(skipped.reason)}
						</li>
					{/each}
				</ul>
			</div>
		{/if}

		{#if phase !== 'done' && blocked !== null}
			<p class="mt-4 text-sm text-amber-800 dark:text-amber-300">{blocked}</p>
		{/if}

		<div class="mt-6 flex justify-end gap-3">
			{#if phase === 'done'}
				<button
					type="button"
					onclick={onclose}
					class="rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
				>
					Close
				</button>
			{:else}
				<button
					type="button"
					onclick={onclose}
					disabled={phase === 'sending'}
					class="rounded-lg px-4 py-2 text-sm font-medium text-surface-600 transition hover:bg-surface-100 disabled:opacity-50 dark:text-surface-400 dark:hover:bg-surface-700"
				>
					Cancel
				</button>
				<button
					type="button"
					onclick={onconfirm}
					disabled={canConfirm === false || phase === 'sending'}
					class="rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
				>
					{phase === 'sending' ? 'Sending...' : confirmLabel}
				</button>
			{/if}
		</div>
	</div>
</div>
