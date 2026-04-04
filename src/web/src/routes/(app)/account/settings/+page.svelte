<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { UserDto } from '$lib/api/types';
	import ThemeToggle from '$lib/components/ThemeToggle.svelte';
	import { ArrowLeft, Info } from 'lucide-svelte';
	import PageHeader from '$lib/components/PageHeader.svelte';

	let { data } = $props();

	const user: UserDto | null = $derived(data.user);
</script>

<div class="space-y-6">
	<!-- Page Header -->
	<div>
		<a
			href="/account"
			class="mb-4 inline-flex items-center gap-1 text-sm text-surface-500 transition hover:text-primary-500 dark:text-surface-400"
		>
			<ArrowLeft class="h-4 w-4" />
			Back to Account
		</a>
		<PageHeader title="Account Settings" description="View your account details and preferences." />
	</div>

	{#if user}
		<!-- Account Info Card -->
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<h2 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">
				Account Information
			</h2>
			<div class="space-y-4">
				<div>
					<span class="block text-sm font-medium text-surface-500 dark:text-surface-400">
						Display Name
					</span>
					<div
						class="mt-1 rounded-lg border border-surface-200 bg-surface-50 px-4 py-2.5 text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
					>
						{user.name}
					</div>
				</div>
				<div>
					<span class="block text-sm font-medium text-surface-500 dark:text-surface-400">
						Email
					</span>
					<div
						class="mt-1 rounded-lg border border-surface-200 bg-surface-50 px-4 py-2.5 text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
					>
						{user.email}
					</div>
				</div>
			</div>
			<div class="mt-4 flex items-start gap-2 rounded-lg bg-surface-50 p-3 dark:bg-surface-900">
				<Info class="mt-0.5 h-4 w-4 shrink-0 text-surface-400" />
				<p class="text-sm text-surface-500 dark:text-surface-400">
					Account details are managed by your identity provider.
				</p>
			</div>
		</div>

		<!-- Theme Preference -->
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<h2 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">
				Theme Preference
			</h2>
			<div class="flex items-center justify-between">
				<div>
					<p class="font-medium text-surface-900 dark:text-surface-100">Appearance</p>
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Toggle between light and dark mode.
					</p>
				</div>
				<ThemeToggle />
			</div>
		</div>
	{:else}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			<p class="text-surface-500 dark:text-surface-400">Unable to load account information.</p>
		</div>
	{/if}
</div>
