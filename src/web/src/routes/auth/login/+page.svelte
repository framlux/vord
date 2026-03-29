<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';
	import { ApiClient } from '$lib/api/client';

	let { data } = $props();
	let returnUrl = $derived(encodeURIComponent(data.returnUrl));

	let email = $state('');
	let emailError = $state('');
	let emailLoading = $state(false);

	const api = new ApiClient('');

	async function handleEmailSso(e: SubmitEvent) {
		e.preventDefault();
		if (email.trim().length === 0) {
			emailError = 'Please enter your work email address';
			return;
		}

		emailLoading = true;
		emailError = '';

		try {
			const result = await api.emailLookup(email.trim());

			window.location.href = `/api/v1/auth/challenge/tenant-oidc?returnUrl=${returnUrl}&tenantId=${result.tenantId}`;
		} catch (err) {
			emailError = err instanceof Error ? err.message : 'An unexpected error occurred';
		} finally {
			emailLoading = false;
		}
	}
</script>

<PublicShell user={null}>
	<div class="flex min-h-[60vh] items-center justify-center">
		<div class="w-full max-w-md rounded-xl border border-surface-200 bg-surface-50 p-8 dark:border-surface-700 dark:bg-surface-800">
			<div class="text-center">
				<h1 class="text-2xl font-bold text-surface-900 dark:text-surface-50">Sign In</h1>
				<p class="mt-2 text-sm text-surface-500">Choose a provider to continue</p>
			</div>

			<div class="mt-8 space-y-3">
				<a
					href="/api/v1/auth/challenge/github?returnUrl={returnUrl}"
					class="flex w-full items-center justify-center gap-3 rounded-lg border border-surface-300 bg-surface-900 px-4 py-3 text-sm font-medium text-white transition hover:bg-surface-800 dark:border-surface-600 dark:bg-surface-700 dark:hover:bg-surface-600"
				>
					<svg class="h-5 w-5" fill="currentColor" viewBox="0 0 24 24"><path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/></svg>
					Continue with GitHub
				</a>

				<a
					href="/api/v1/auth/challenge/google?returnUrl={returnUrl}"
					class="flex w-full items-center justify-center gap-3 rounded-lg border border-surface-300 bg-surface-50 px-4 py-3 text-sm font-medium text-surface-700 transition hover:bg-surface-50 dark:border-surface-600 dark:bg-surface-800 dark:text-surface-200 dark:hover:bg-surface-700"
				>
					<svg class="h-5 w-5" viewBox="0 0 24 24"><path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 0 1-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z"/><path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/><path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"/><path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"/></svg>
					Continue with Google
				</a>

				<a
					href="/api/v1/auth/challenge/microsoft?returnUrl={returnUrl}"
					class="flex w-full items-center justify-center gap-3 rounded-lg border border-surface-300 bg-surface-50 px-4 py-3 text-sm font-medium text-surface-700 transition hover:bg-surface-50 dark:border-surface-600 dark:bg-surface-800 dark:text-surface-200 dark:hover:bg-surface-700"
				>
					<svg class="h-5 w-5" viewBox="0 0 24 24"><path fill="#F25022" d="M1 1h10v10H1z"/><path fill="#00A4EF" d="M1 13h10v10H1z"/><path fill="#7FBA00" d="M13 1h10v10H13z"/><path fill="#FFB900" d="M13 13h10v10H13z"/></svg>
					Continue with Microsoft
				</a>
			</div>

			{#if data.tenant}
				<div class="mt-4 border-t border-surface-200 pt-4 dark:border-surface-700">
					<a
						href="/api/v1/auth/challenge/tenant-oidc?returnUrl={returnUrl}&tenantId={data.tenant}"
						class="flex w-full items-center justify-center gap-3 rounded-lg border border-primary-500 bg-primary-500/10 px-4 py-3 text-sm font-medium text-primary-600 transition hover:bg-primary-500/20 dark:text-primary-400"
					>
						Sign in with SSO
					</a>
				</div>
			{/if}

			<div class="mt-6 border-t border-surface-200 pt-6 dark:border-surface-700">
				<p class="mb-3 text-center text-sm text-surface-500">Or sign in with your organization's SSO</p>
				<form onsubmit={handleEmailSso} class="space-y-3">
					<input
						type="email"
						bind:value={email}
						placeholder="you@company.com"
						class="w-full rounded-lg border border-surface-300 bg-surface-50 px-4 py-2.5 text-sm text-surface-900 placeholder-surface-400 focus:border-primary-500 focus:outline-none focus:ring-1 focus:ring-primary-500 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100 dark:placeholder-surface-500"
					/>

					{#if emailError}
						<div class="rounded-lg bg-red-50 p-3 text-sm text-red-600 dark:bg-red-900/20 dark:text-red-400">
							{emailError}
						</div>
					{/if}

					<button
						type="submit"
						disabled={emailLoading}
						class="w-full rounded-lg border border-primary-500 bg-primary-500/10 px-4 py-2.5 text-sm font-medium text-primary-600 transition hover:bg-primary-500/20 disabled:cursor-not-allowed disabled:opacity-50 dark:text-primary-400"
					>
						{emailLoading ? 'Looking up...' : 'Continue with SSO'}
					</button>
				</form>
			</div>

			<p class="mt-6 text-center text-xs text-surface-400">
				By signing in, you agree to our
				<a href="/terms" class="text-primary-500 hover:underline">Terms of Service</a> and
				<a href="/privacy" class="text-primary-500 hover:underline">Privacy Policy</a>.
			</p>
		</div>
	</div>
</PublicShell>
