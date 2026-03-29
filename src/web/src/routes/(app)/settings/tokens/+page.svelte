<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import { ApiClient } from '$lib/api/client';
	import type { RegistrationTokenDto } from '$lib/api/types';
	import { Copy, XCircle, Check, Plus } from 'lucide-svelte';

	let { data } = $props();

	let tokens: RegistrationTokenDto[] = $derived(data.tokens);

	let tokenName = $state('');
	let expiresInDays = $state(90);
	let maxUses = $state(100);
	let createError = $state('');
	let createLoading = $state(false);
	let newToken = $state<RegistrationTokenDto | null>(null);
	let copied = $state(false);

	const client = new ApiClient('');

	async function createToken() {
		createError = '';

		if (!tokenName.trim()) {
			createError = 'Please enter a token name.';

			return;
		}

		createLoading = true;
		try {
			const result = await client.createRegistrationToken({
				name: tokenName.trim(),
				expiresInDays,
				maxUses
			});
			newToken = result;
			tokenName = '';
			await invalidateAll();
		} catch (err: unknown) {
			createError = err instanceof Error ? err.message : 'Failed to create token';
		} finally {
			createLoading = false;
		}
	}

	async function revokeToken(id: number) {
		try {
			await client.revokeRegistrationToken(id);
			await invalidateAll();
		} catch (err: unknown) {
			createError = err instanceof Error ? err.message : 'Failed to revoke token';
		}
	}

	function copyToClipboard(text: string) {
		navigator.clipboard.writeText(text);
		copied = true;
		setTimeout(() => (copied = false), 2000);
	}

	function formatDate(dateStr: string): string {
		return new Date(dateStr).toLocaleDateString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric'
		});
	}

	function isExpired(expiresAt: string): boolean {
		return new Date(expiresAt) < new Date();
	}

	const activeTokens = $derived(tokens.filter(t => !t.isRevoked && !isExpired(t.expiresAt)));
	const inactiveTokens = $derived(tokens.filter(t => t.isRevoked || isExpired(t.expiresAt)));
</script>

<div class="mx-auto max-w-4xl">
	<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">Registration Tokens</h1>
	<p class="mt-1 text-sm text-surface-500">
		Manage tokens used to register machines with your organization. Deploy a token to a machine to allow it to auto-enroll.
	</p>

	<!-- Create token form -->
	<div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
		<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Create Token</h2>
		<p class="mt-1 text-sm text-surface-500">
			The token value is shown only once after creation. Copy it and deploy it to your machines.
		</p>

		<form onsubmit={(e) => { e.preventDefault(); createToken(); }} class="mt-4 space-y-4">
			<div class="flex gap-3">
				<div class="flex-1">
					<label for="token-name" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Name</label>
					<input
						id="token-name"
						type="text"
						bind:value={tokenName}
						placeholder="e.g. Production Servers"
						class="mt-1 w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 placeholder:text-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
					/>
				</div>
				<div class="w-32">
					<label for="expires-days" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Expires (days)</label>
					<input
						id="expires-days"
						type="number"
						bind:value={expiresInDays}
						min="1"
						max="365"
						class="mt-1 w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
					/>
				</div>
				<div class="w-32">
					<label for="max-uses" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Max uses</label>
					<input
						id="max-uses"
						type="number"
						bind:value={maxUses}
						min="1"
						max="10000"
						class="mt-1 w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
					/>
				</div>
			</div>
			<button
				type="submit"
				disabled={createLoading}
				class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
			>
				<Plus size={16} />
				{createLoading ? 'Creating...' : 'Create Token'}
			</button>
		</form>

		{#if createError}
			<p class="mt-3 text-sm text-red-600 dark:text-red-400">{createError}</p>
		{/if}

		{#if newToken?.token}
			<div class="mt-4 rounded-lg border border-green-200 bg-green-50 p-4 dark:border-green-800 dark:bg-green-900/20">
				<div class="flex items-center gap-2">
					<Check size={16} class="text-green-600 dark:text-green-400" />
					<span class="text-sm font-medium text-green-700 dark:text-green-300">Token created successfully</span>
				</div>
				<p class="mt-1 text-xs text-green-600 dark:text-green-400">
					Copy this token now. It will not be shown again.
				</p>
				<div class="mt-2 flex items-center gap-2">
					<code class="flex-1 rounded bg-green-100 px-3 py-2 text-xs font-mono text-green-800 dark:bg-green-900/40 dark:text-green-200">
						{newToken.token}
					</code>
					<button
						onclick={() => copyToClipboard(newToken?.token ?? '')}
						class="inline-flex items-center gap-1 rounded px-3 py-2 text-xs font-medium text-green-700 hover:bg-green-100 dark:text-green-300 dark:hover:bg-green-800/30"
					>
						<Copy size={14} />
						{copied ? 'Copied' : 'Copy'}
					</button>
				</div>
			</div>
		{/if}
	</div>

	<!-- Active tokens -->
	<div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
		<div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Active Tokens</h2>
		</div>
		{#if activeTokens.length === 0}
			<p class="px-6 py-8 text-center text-sm text-surface-500">No active registration tokens.</p>
		{:else}
			<div class="divide-y divide-surface-100 dark:divide-surface-700">
				{#each activeTokens as token (token.id)}
					<div class="flex items-center justify-between px-6 py-4">
						<div>
							<p class="text-sm font-medium text-surface-900 dark:text-surface-50">{token.name}</p>
							<p class="text-xs text-surface-500">
								Created {formatDate(token.createdAt)} &middot;
								Expires {formatDate(token.expiresAt)} &middot;
								Used {token.usedCount}/{token.maxUses}
							</p>
						</div>
						<button
							onclick={() => revokeToken(token.id)}
							class="inline-flex items-center gap-1 rounded px-2 py-1 text-xs text-surface-500 hover:bg-surface-100 hover:text-red-600 dark:hover:bg-surface-700 dark:hover:text-red-400"
							title="Revoke token"
						>
							<XCircle size={14} />
							Revoke
						</button>
					</div>
				{/each}
			</div>
		{/if}
	</div>

	<!-- Inactive tokens -->
	{#if inactiveTokens.length > 0}
		<div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
			<div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Inactive Tokens</h2>
			</div>
			<div class="divide-y divide-surface-100 dark:divide-surface-700">
				{#each inactiveTokens as token (token.id)}
					<div class="flex items-center justify-between px-6 py-4">
						<div>
							<p class="text-sm font-medium text-surface-900 dark:text-surface-50">{token.name}</p>
							<p class="text-xs text-surface-500">
								Created {formatDate(token.createdAt)} &middot;
								{#if token.isRevoked}
									<span class="text-red-600 dark:text-red-400">Revoked</span>
								{:else}
									<span class="text-amber-600 dark:text-amber-400">Expired</span>
								{/if}
								&middot; Used {token.usedCount}/{token.maxUses}
							</p>
						</div>
						<span class="rounded-full px-2 py-0.5 text-xs font-medium {token.isRevoked ? 'bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400' : 'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400'}">
							{token.isRevoked ? 'Revoked' : 'Expired'}
						</span>
					</div>
				{/each}
			</div>
		</div>
	{/if}
</div>
