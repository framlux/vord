<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { goto } from '$app/navigation';
	import { ApiClient } from '$lib/api/client';
	import { AlertTriangle, CircleCheck, XCircle, Clock } from 'lucide-svelte';
	import PublicShell from '$lib/components/PublicShell.svelte';

	let { data } = $props();

	let accepting = $state(false);
	let error = $state('');
	let accepted = $state(false);

	const client = new ApiClient('');

	const invitation = $derived(data.invitation);
	const token = $derived(data.token);
	const user = $derived(data.user);

	const emailMatch = $derived(invitation && user
		? user.email.toLowerCase() === invitation.email.toLowerCase()
		: false);

	const isExpired = $derived(invitation
		? invitation.status === 'Expired' || new Date(invitation.expiresAt) < new Date()
		: false);

	const canAccept = $derived(invitation
		&& invitation.status === 'Pending'
		&& isExpired === false
		&& emailMatch);

	async function acceptInvitation() {
		if (token === null) return;

		accepting = true;
		error = '';

		try {
			await client.acceptInvitation(token);
			accepted = true;
			setTimeout(() => goto('/dashboard'), 1500);
		} catch (err: unknown) {
			error = err instanceof Error ? err.message : 'Failed to accept invitation';
		} finally {
			accepting = false;
		}
	}
</script>

<svelte:head><title>Accept Invitation - Vord</title></svelte:head>

<PublicShell user={user}>
	<div class="flex min-h-screen items-center justify-center px-4">
		<div class="w-full max-w-md rounded-xl border border-surface-200 bg-surface-50 p-8 shadow-sm dark:border-surface-700 dark:bg-surface-800">
			{#if invitation === null}
				<div class="text-center">
					<XCircle size={48} class="mx-auto text-red-500" />
					<h1 class="mt-4 text-xl font-bold text-surface-900 dark:text-surface-50">Invitation Not Found</h1>
					<p class="mt-2 text-sm text-surface-500">This invitation link is invalid or has been removed.</p>
					<a href="/dashboard" class="mt-6 inline-block rounded-lg bg-primary-500 px-6 py-2 text-sm font-medium text-white hover:bg-primary-600">
						Go to Dashboard
					</a>
				</div>
			{:else if accepted}
				<div class="text-center">
					<CircleCheck size={48} class="mx-auto text-green-500" />
					<h1 class="mt-4 text-xl font-bold text-surface-900 dark:text-surface-50">Welcome!</h1>
					<p class="mt-2 text-sm text-surface-500">You've joined <strong>{invitation.tenantName}</strong>. Redirecting to dashboard...</p>
				</div>
			{:else}
				<div class="text-center">
					<h1 class="text-xl font-bold text-surface-900 dark:text-surface-50">You've been invited</h1>
					<p class="mt-2 text-sm text-surface-500">
						<strong>{invitation.inviterEmail}</strong> invited you to join
					</p>
					<p class="mt-1 text-lg font-semibold text-surface-900 dark:text-surface-50">{invitation.tenantName}</p>
				</div>

				<div class="mt-6 space-y-3 rounded-lg bg-surface-50 p-4 dark:bg-surface-700/50">
					<div class="flex items-center justify-between text-sm">
						<span class="text-surface-500">Invited email</span>
						<span class="font-medium text-surface-900 dark:text-surface-50">{invitation.email}</span>
					</div>
					<div class="flex items-center justify-between text-sm">
						<span class="text-surface-500">Your email</span>
						<span class="font-medium {emailMatch ? 'text-green-600 dark:text-green-400' : 'text-red-600 dark:text-red-400'}">{user?.email ?? 'Unknown'}</span>
					</div>
					<div class="flex items-center justify-between text-sm">
						<span class="text-surface-500">Expires</span>
						<span class="font-medium text-surface-900 dark:text-surface-50">
							{new Date(invitation.expiresAt).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
						</span>
					</div>
				</div>

				{#if isExpired}
					<div class="mt-4 flex items-center gap-2 rounded-lg bg-amber-50 px-4 py-3 dark:bg-amber-900/20">
						<Clock size={16} class="text-amber-600 dark:text-amber-400" />
						<span class="text-sm text-amber-700 dark:text-amber-300">This invitation has expired. Ask the admin to resend it.</span>
					</div>
				{:else if invitation.status !== 'Pending'}
					<div class="mt-4 flex items-center gap-2 rounded-lg bg-surface-100 px-4 py-3 dark:bg-surface-700">
						<XCircle size={16} class="text-surface-500" />
						<span class="text-sm text-surface-600 dark:text-surface-400">This invitation has been {invitation.status.toLowerCase()}.</span>
					</div>
				{:else if emailMatch === false}
					<div class="mt-4 flex items-center gap-2 rounded-lg bg-red-50 px-4 py-3 dark:bg-red-900/20">
						<AlertTriangle size={16} class="text-red-600 dark:text-red-400" />
						<span class="text-sm text-red-700 dark:text-red-300">Your email doesn't match. Sign in with <strong>{invitation.email}</strong> to accept.</span>
					</div>
				{/if}

				{#if error}
					<p class="mt-4 text-sm text-red-600 dark:text-red-400">{error}</p>
				{/if}

				<div class="mt-6 flex gap-3">
					<a href="/dashboard" class="flex-1 rounded-lg border border-surface-300 px-4 py-2.5 text-center text-sm font-medium text-surface-700 hover:bg-surface-50 dark:border-surface-600 dark:text-surface-300 dark:hover:bg-surface-700">
						Decline
					</a>
					<button
						onclick={acceptInvitation}
						disabled={canAccept === false || accepting}
						class="flex-1 rounded-lg bg-primary-500 px-4 py-2.5 text-sm font-medium text-white transition hover:bg-primary-600 disabled:opacity-50"
					>
						{accepting ? 'Accepting...' : 'Accept Invitation'}
					</button>
				</div>
			{/if}
		</div>
	</div>
</PublicShell>
