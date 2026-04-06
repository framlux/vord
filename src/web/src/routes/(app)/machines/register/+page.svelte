<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
    import { invalidateAll } from '$app/navigation';
    import { ApiClient } from '$lib/api/client';
    import type { RegistrationTokenDto } from '$lib/api/types';
    import { Copy, XCircle, Check, Plus, Terminal } from 'lucide-svelte';
    import InstallScriptModal from '$lib/components/InstallScriptModal.svelte';
    import PageHeader from '$lib/components/PageHeader.svelte';

    let { data } = $props();

    let tokens: RegistrationTokenDto[] = $derived(data.tokens);

    let tokenName = $state('');
    let createError = $state('');
    let createLoading = $state(false);
    let newToken = $state<RegistrationTokenDto | null>(null);
    let copied = $state(false);
    let showInstallScript = $state(false);

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
                name: tokenName.trim()
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

    const activeTokens = $derived(tokens.filter(t => t.isRevoked === false));
    const inactiveTokens = $derived(tokens.filter(t => t.isRevoked));
</script>

<div class="mx-auto max-w-4xl space-y-6">
    <PageHeader title="Registration Tokens" description="Manage tokens used to register machines with your organization. Deploy a token to a machine to allow it to auto-enroll." />

    <!-- Create token form -->
    <div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
        <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Create Token</h2>
        <p class="mt-1 text-sm text-surface-500">
            The token value is shown only once after creation. Copy it and deploy it to your machines.
        </p>

        <form onsubmit={(e) => { e.preventDefault(); createToken(); }} class="mt-4 space-y-4">
            <div>
                <label for="token-name" class="block text-xs font-medium text-surface-600 dark:text-surface-400">Name</label>
                <input
                    id="token-name"
                    type="text"
                    bind:value={tokenName}
                    placeholder="e.g. Production Servers"
                    class="mt-1 w-full max-w-md rounded-lg border border-surface-300 bg-surface-50 px-4 py-2 text-sm text-surface-900 placeholder:text-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-50"
                />
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
                    <button
                        onclick={() => showInstallScript = true}
                        class="inline-flex items-center gap-1 rounded px-3 py-2 text-xs font-medium text-primary-700 hover:bg-primary-100 dark:text-primary-300 dark:hover:bg-primary-800/30"
                        title="Generate a ready-to-run install script with this token"
                    >
                        <Terminal size={14} />
                        Install Script
                    </button>
                </div>
            </div>
        {/if}
    </div>

    <!-- Active tokens -->
    <div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
        <div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
            <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Active Tokens <span class="text-xs font-normal text-surface-400">({activeTokens.length})</span></h2>
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
                                Created {formatDate(token.createdAt)}
                            </p>
                        </div>
                        <button
                            onclick={() => revokeToken(token.id)}
                            class="inline-flex items-center gap-1 rounded-md border border-surface-200 px-2.5 py-1 text-xs text-surface-500 hover:bg-surface-100 hover:text-red-600 dark:border-surface-700 dark:hover:bg-surface-700 dark:hover:text-red-400"
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

    <!-- Revoked tokens -->
    {#if inactiveTokens.length > 0}
        <div class="mt-8 rounded-lg border border-surface-200 bg-surface-50 dark:border-surface-700 dark:bg-surface-800">
            <div class="border-b border-surface-200 px-6 py-4 dark:border-surface-700">
                <h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Revoked Tokens <span class="text-xs font-normal text-surface-400">({inactiveTokens.length})</span></h2>
            </div>
            <div class="divide-y divide-surface-100 dark:divide-surface-700">
                {#each inactiveTokens as token (token.id)}
                    <div class="flex items-center justify-between px-6 py-4">
                        <div>
                            <p class="text-sm font-medium text-surface-900 dark:text-surface-50">{token.name}</p>
                            <p class="text-xs text-surface-500">
                                Created {formatDate(token.createdAt)}
                            </p>
                        </div>
                        <span class="rounded-full bg-red-100 px-2 py-0.5 text-xs font-medium text-red-700 dark:bg-red-900/30 dark:text-red-400">
                            Revoked
                        </span>
                    </div>
                {/each}
            </div>
        </div>
    {/if}
</div>

<InstallScriptModal
    open={showInstallScript}
    token={newToken?.token ?? ''}
    onclose={() => showInstallScript = false}
/>
