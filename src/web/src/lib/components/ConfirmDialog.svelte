<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { moveFocusInto, trapTabKey } from '$lib/utils/focus-trap';

	let {
		open = false,
		title = 'Confirm',
		message = 'Are you sure?',
		confirmLabel = 'Confirm',
		cancelLabel = 'Cancel',
		variant = 'danger' as 'danger' | 'warning' | 'info',
		onconfirm,
		oncancel
	}: {
		open?: boolean;
		title?: string;
		message?: string;
		confirmLabel?: string;
		cancelLabel?: string;
		variant?: 'danger' | 'warning' | 'info';
		onconfirm?: () => void;
		oncancel?: () => void;
	} = $props();

	let dialogElement: HTMLDivElement | undefined = $state(undefined);
	let previouslyFocused: HTMLElement | null = null;

	$effect(() => {
		if (open) {
			previouslyFocused = document.activeElement as HTMLElement;
			requestAnimationFrame(() => {
				if (dialogElement !== undefined) {
					moveFocusInto(dialogElement);
				}
			});
		} else if (previouslyFocused !== null) {
			previouslyFocused.focus();
			previouslyFocused = null;
		}
	});

	// Was a local copy that collected buttons alone. That is adequate for the two buttons this
	// dialog actually renders, but it is the copy other dialogs were modelled on, and there it
	// skipped every input, select and checkbox. One tested implementation instead of four.
	function trapFocus(event: KeyboardEvent) {
		if (dialogElement !== undefined) {
			trapTabKey(dialogElement, event);
		}
	}

	const btnClass: Record<string, string> = {
		danger: 'bg-error-500 hover:bg-red-600 text-white',
		warning: 'bg-warning-500 hover:bg-yellow-600 text-black',
		info: 'bg-primary-500 hover:bg-primary-600 text-white'
	};
</script>

<svelte:window onkeydown={(e) => {
	if (open && e.key === 'Escape') {
		oncancel?.();
	}
}} />

{#if open}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50" role="presentation">
		<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
		<div
			bind:this={dialogElement}
			onkeydown={trapFocus}
			tabindex="-1"
			class="w-full max-w-md rounded-xl bg-surface-50 p-6 shadow-xl dark:bg-surface-800"
			role="dialog"
			aria-modal="true"
			aria-labelledby="confirm-dialog-title"
		>
			<h3 id="confirm-dialog-title" class="text-lg font-semibold text-surface-900 dark:text-surface-50">
				{title}
			</h3>
			<p class="mt-2 text-sm text-surface-600 dark:text-surface-400">{message}</p>
			<div class="mt-6 flex justify-end gap-3">
				<button
					onclick={oncancel}
					class="rounded-lg px-4 py-2 text-sm font-medium text-surface-600 transition hover:bg-surface-100 dark:text-surface-400 dark:hover:bg-surface-700"
				>
					{cancelLabel}
				</button>
				<button
					onclick={onconfirm}
					class="rounded-lg px-4 py-2 text-sm font-medium transition {btnClass[variant]}"
				>
					{confirmLabel}
				</button>
			</div>
		</div>
	</div>
{/if}
