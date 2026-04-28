<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
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
				const firstButton = dialogElement?.querySelector('button');
				firstButton?.focus();
			});
		} else if (previouslyFocused) {
			previouslyFocused.focus();
			previouslyFocused = null;
		}
	});

	function trapFocus(event: KeyboardEvent) {
		if (event.key !== 'Tab') return;

		const focusable = dialogElement?.querySelectorAll('button') ?? [];
		if (focusable.length === 0) return;

		const first = focusable[0] as HTMLElement;
		const last = focusable[focusable.length - 1] as HTMLElement;

		if (event.shiftKey && document.activeElement === first) {
			event.preventDefault();
			last.focus();
		} else if (event.shiftKey === false && document.activeElement === last) {
			event.preventDefault();
			first.focus();
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
