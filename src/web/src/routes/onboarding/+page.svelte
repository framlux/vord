<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';

	let { data } = $props();

	let organizationName = $state('');
	let loading = $state(false);
	let error = $state('');

	const blockedCharsPattern = /[<>"'`\\/{}\|\x00-\x1F]/;

	async function handleSubmit(e: SubmitEvent) {
		e.preventDefault();
		const trimmed = organizationName.trim();
		if (trimmed === '') {
			error = 'Organization name is required';

			return;
		}

		if (trimmed.length < 5) {
			error = 'Organization name must be at least 5 characters';

			return;
		}

		if (blockedCharsPattern.test(trimmed)) {
			error = 'Organization name contains invalid characters';

			return;
		}

		loading = true;
		error = '';

		try {
			const response = await fetch('/api/v1/onboarding/create-org', {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				credentials: 'include',
				body: JSON.stringify({
					organizationName: organizationName.trim()
				})
			});

			const result = await response.json();

			if (response.ok === false) {
				error = result.message ?? 'Failed to create organization';

				return;
			}

			// Purge the in-memory session cache so the full-page redirect fetches fresh user data
			await fetch('/api/session/purge', { method: 'POST', credentials: 'include' });

			// Full page reload to refresh the session cache with new tenant roles
			window.location.href = '/onboarding/success';
		} catch {
			error = 'An unexpected error occurred';
		} finally {
			loading = false;
		}
	}
</script>

<svelte:head><title>Get Started - Vord</title></svelte:head>

<PublicShell user={data.user}>
	<div class="flex min-h-[60vh] items-center justify-center py-12">
		<div class="w-full max-w-lg rounded-xl border border-surface-200 bg-surface-50 p-8 dark:border-surface-700 dark:bg-surface-800">
			<div class="text-center">
				<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">Create Your Organization</h1>
				<p class="mt-2 text-sm text-surface-500">
					Get started with fleet management
				</p>
			</div>

			<form onsubmit={handleSubmit} class="mt-8 space-y-6">
				<div>
					<label for="orgName" class="block text-sm font-medium text-surface-700 dark:text-surface-300">
						Organization Name
					</label>
					<input
						id="orgName"
						type="text"
						bind:value={organizationName}
						placeholder="My Company"
						minlength="5"
					maxlength="100"
						class="mt-1 w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2.5 text-sm text-surface-900 placeholder-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100 dark:placeholder-surface-500"
					/>
				</div>

				{#if error}
					<div class="rounded-lg bg-red-50 p-3 text-sm text-red-600 dark:bg-red-900/20 dark:text-red-400">
						{error}
					</div>
				{/if}

				<button
					type="submit"
					disabled={loading}
					class="w-full rounded-lg bg-primary-500 px-4 py-2.5 text-sm font-medium text-white transition hover:bg-primary-600 disabled:cursor-not-allowed disabled:opacity-50"
				>
					{loading ? 'Creating...' : 'Create Organization'}
				</button>
			</form>

			<div class="mt-6 rounded-lg bg-surface-50 p-4 dark:bg-surface-900">
				<h3 class="text-sm font-medium text-surface-700 dark:text-surface-300">
					Your plan includes:
				</h3>
				<ul class="mt-2 space-y-1 text-sm text-surface-500">
					<li>Dashboard & monitoring</li>
					<li>Hardware health / SMART</li>
					<li>SSH session monitoring</li>
					<li>Social login</li>
				</ul>
			</div>
		</div>
	</div>
</PublicShell>
