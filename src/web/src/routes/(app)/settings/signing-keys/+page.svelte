<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { invalidateAll } from '$app/navigation';
	import { ApiClient } from '$lib/api/client';
	import type { SigningKeyDto, SigningKeyListResponse } from '$lib/api/types';
	import { generateKeyPair, autoLabel } from '$lib/crypto/signing';
	import { Key, XCircle, Plus, Shield, AlertTriangle } from 'lucide-svelte';

	let { data } = $props();

	let signingKeys: SigningKeyListResponse = $derived(data.signingKeys);

	let keyLabel = $state('');
	let createError = $state('');
	let createLoading = $state(false);
	let revokeError = $state('');

	const client = new ApiClient('');

	async function generateAndRegisterKey() {
		createError = '';
		const label = keyLabel.trim() || autoLabel();
		createLoading = true;

		try {
			const userId = data.user.id;
			const tenantId = data.user.activeTenantId;
			if (tenantId === null || tenantId === undefined) {
				createError = 'No active tenant selected.';

				return;
			}

			const { publicKeyBase64 } = await generateKeyPair(userId, tenantId, label);

			await client.registerSigningKey({ label, publicKey: publicKeyBase64 });
			keyLabel = '';
			await invalidateAll();
		} catch (err: unknown) {
			createError = err instanceof Error ? err.message : 'Failed to generate key';
		} finally {
			createLoading = false;
		}
	}

	async function revokeKey(keyId: number) {
		revokeError = '';
		try {
			await client.revokeSigningKey(keyId);
			await invalidateAll();
		} catch (err: unknown) {
			revokeError = err instanceof Error ? err.message : 'Failed to revoke key';
		}
	}

	function formatDate(dateStr: string): string {
		return new Date(dateStr).toLocaleDateString('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric'
		});
	}

	function truncateFingerprint(fp: string): string {
		return fp.length > 16 ? fp.slice(0, 16) + '...' : fp;
	}

	const activeKeys = $derived(signingKeys.keys.filter((k) => k.revokedAt === null));
	const revokedKeys = $derived(signingKeys.keys.filter((k) => k.revokedAt));
	const atLimit = $derived(signingKeys.activeCount >= signingKeys.maxKeys);
</script>

<div class="mx-auto max-w-4xl">
	<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">Signing Keys</h1>
	<p class="mt-1 text-sm text-surface-500">
		Ed25519 signing keys authorize remote commands sent to your machines. Private keys never leave
		your browser.
	</p>

	<!-- Generate key section -->
	<div
		class="mt-8 rounded-lg border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="flex items-center gap-2">
			<Shield size={20} class="text-primary-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Generate Key</h2>
		</div>
		<p class="mt-1 text-sm text-surface-500">
			Each device gets its own key. The private key is stored securely in your browser and cannot be
			exported.
		</p>

		{#if atLimit}
			<div
				class="mt-4 flex items-center gap-2 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-700 dark:bg-amber-900/20"
			>
				<AlertTriangle size={16} class="text-amber-600 dark:text-amber-400" />
				<span class="text-sm text-amber-700 dark:text-amber-300">
					Maximum of {signingKeys.maxKeys} active keys reached. Revoke an existing key to register
					a new one.
				</span>
			</div>
		{:else}
			<form
				onsubmit={(e) => {
					e.preventDefault();
					generateAndRegisterKey();
				}}
				class="mt-4 flex items-end gap-3"
			>
				<div class="flex-1">
					<label
						for="key-label"
						class="block text-xs font-medium text-surface-600 dark:text-surface-400"
						>Label (optional)</label
					>
					<input
						id="key-label"
						type="text"
						bind:value={keyLabel}
						placeholder={autoLabel()}
						class="mt-1 w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 placeholder:text-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
					/>
				</div>
				<button
					type="submit"
					disabled={createLoading}
					class="inline-flex items-center gap-2 rounded-lg bg-primary-500 px-4 py-2 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
				>
					<Plus size={16} />
					{createLoading ? 'Generating...' : 'Generate Key'}
				</button>
			</form>
		{/if}

		{#if createError}
			<p class="mt-3 text-sm text-red-600 dark:text-red-400">{createError}</p>
		{/if}
	</div>

	<!-- Active keys -->
	<div
		class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="flex items-center justify-between border-b border-surface-200 px-6 py-4 dark:border-surface-700">
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Active Keys</h2>
			<span class="text-xs text-surface-500"
				>{signingKeys.activeCount}/{signingKeys.maxKeys} used</span
			>
		</div>
		{#if activeKeys.length === 0}
			<p class="px-6 py-8 text-center text-sm text-surface-500">
				No active signing keys. Generate one to start sending remote commands.
			</p>
		{:else}
			<div class="divide-y divide-surface-100 dark:divide-surface-700">
				{#each activeKeys as key (key.id)}
					<div class="flex items-center justify-between px-6 py-4">
						<div class="flex items-center gap-3">
							<Key size={16} class="text-surface-400" />
							<div>
								<p class="text-sm font-medium text-surface-900 dark:text-surface-50">
									{key.label}
								</p>
								<p class="text-xs text-surface-500">
									<span class="font-mono">{truncateFingerprint(key.fingerprint)}</span>
									&middot; Created {formatDate(key.createdAt)}
								</p>
							</div>
						</div>
						<button
							onclick={() => revokeKey(key.id)}
							class="inline-flex items-center gap-1 rounded px-2 py-1 text-xs text-surface-500 hover:bg-surface-100 hover:text-red-600 dark:hover:bg-surface-700 dark:hover:text-red-400"
							title="Revoke key"
						>
							<XCircle size={14} />
							Revoke
						</button>
					</div>
				{/each}
			</div>
		{/if}
	</div>

	{#if revokeError}
		<p class="mt-3 text-sm text-red-600 dark:text-red-400">{revokeError}</p>
	{/if}

	<!-- Revoked keys -->
	{#if revokedKeys.length > 0}
		<div
			class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800"
		>
			<div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Revoked Keys</h2>
			</div>
			<div class="divide-y divide-surface-100 dark:divide-surface-700">
				{#each revokedKeys as key (key.id)}
					<div class="flex items-center justify-between px-6 py-4 opacity-60">
						<div class="flex items-center gap-3">
							<Key size={16} class="text-surface-400" />
							<div>
								<p class="text-sm font-medium text-surface-900 dark:text-surface-50">
									{key.label}
								</p>
								<p class="text-xs text-surface-500">
									<span class="font-mono">{truncateFingerprint(key.fingerprint)}</span>
									&middot; Revoked {formatDate(key.revokedAt!)}
								</p>
							</div>
						</div>
						<span
							class="rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-700 dark:bg-red-900/30 dark:text-red-400"
						>
							Revoked
						</span>
					</div>
				{/each}
			</div>
		</div>
	{/if}
</div>
