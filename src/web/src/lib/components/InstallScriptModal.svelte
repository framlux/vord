<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { generateInstallScript } from '$lib/utils/install-script';
	import { Copy, Check, X } from 'lucide-svelte';

	let {
		open = false,
		token = '',
		serverAddress = 'grpc.app.vordfleet.dev',
		onclose
	}: {
		open?: boolean;
		token?: string;
		serverAddress?: string;
		onclose?: () => void;
	} = $props();

	let copied = $state(false);

	const script = $derived(generateInstallScript(token, serverAddress));

	function copyScript() {
		navigator.clipboard.writeText(script);
		copied = true;
		setTimeout(() => (copied = false), 2000);
	}
</script>

{#if open}
	<div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50" role="presentation">
		<div
			class="mx-4 w-full max-w-3xl rounded-xl bg-surface-50 p-6 shadow-xl dark:bg-surface-800"
			role="dialog"
			aria-modal="true"
			aria-labelledby="install-script-dialog-title"
		>
			<div class="flex items-center justify-between">
				<h3 id="install-script-dialog-title" class="text-lg font-semibold text-surface-900 dark:text-surface-50">
					Install Script
				</h3>
				<button
					onclick={onclose}
					class="rounded-lg p-1 text-surface-500 transition hover:bg-surface-100 dark:hover:bg-surface-700"
					aria-label="Close"
				>
					<X size={20} />
				</button>
			</div>
			<p class="mt-1 text-sm text-surface-500">
				Run this script on your target machine to install and configure the agent automatically.
			</p>
			<div class="mt-4 max-h-96 overflow-y-auto rounded-lg border border-surface-200 bg-surface-900 dark:border-surface-600 dark:bg-surface-950">
				<pre class="overflow-x-auto p-4 text-xs leading-relaxed text-surface-100"><code>{script}</code></pre>
			</div>
			<div class="mt-4 flex justify-end gap-3">
				<button
					onclick={onclose}
					class="rounded-lg px-4 py-2 text-sm font-medium text-surface-600 transition hover:bg-surface-100 dark:text-surface-400 dark:hover:bg-surface-700"
				>
					Close
				</button>
				<button
					onclick={copyScript}
					class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600"
				>
					{#if copied}
						<Check size={16} />
						Copied
					{:else}
						<Copy size={16} />
						Copy Script
					{/if}
				</button>
			</div>
		</div>
	</div>
{/if}
