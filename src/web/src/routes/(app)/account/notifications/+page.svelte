<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { ArrowLeft, Info } from 'lucide-svelte';

	let newMachineRegistered = $state(true);
	let machineApprovalRequired = $state(true);
	let certificateExpiring = $state(false);
	let userRegistration = $state(false);
</script>

<svelte:head><title>Notifications - Vord</title></svelte:head>

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
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">
			Notification Preferences
		</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Choose which notifications you would like to receive.
		</p>
	</div>

	<!-- Coming Soon Banner -->
	<div class="mb-6 rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/20">
		<p class="text-sm text-amber-800 dark:text-amber-300">
			Notification preferences will be available in a future update. Settings shown below are not yet active.
		</p>
	</div>

	<!-- Notification Toggles -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="divide-y divide-surface-100 dark:divide-surface-700">
			<!-- New Machine Registered -->
			<div class="flex items-center justify-between py-4 first:pt-0 last:pb-0">
				<div>
					<p class="font-medium text-surface-900 dark:text-surface-100">
						New Machine Registered
					</p>
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Get notified when a new machine registers with the platform.
					</p>
				</div>
				<label class="relative inline-flex cursor-pointer items-center">
					<input
						type="checkbox"
						bind:checked={newMachineRegistered}
						disabled
						class="peer sr-only"
					/>
					<div
						class="h-6 w-10 rounded-full bg-surface-300 after:absolute after:left-[2px] after:top-[2px] after:h-5 after:w-5 after:rounded-full after:bg-surface-50 after:transition-all after:content-[''] peer-checked:bg-primary-500 peer-checked:after:translate-x-4 dark:bg-surface-600 dark:after:bg-surface-200"
					></div>
				</label>
			</div>

			<!-- Machine Approval Required -->
			<div class="flex items-center justify-between py-4 first:pt-0 last:pb-0">
				<div>
					<p class="font-medium text-surface-900 dark:text-surface-100">
						Machine Approval Required
					</p>
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Get notified when a machine requires approval.
					</p>
				</div>
				<label class="relative inline-flex cursor-pointer items-center">
					<input
						type="checkbox"
						bind:checked={machineApprovalRequired}
						disabled
						class="peer sr-only"
					/>
					<div
						class="h-6 w-10 rounded-full bg-surface-300 after:absolute after:left-[2px] after:top-[2px] after:h-5 after:w-5 after:rounded-full after:bg-surface-50 after:transition-all after:content-[''] peer-checked:bg-primary-500 peer-checked:after:translate-x-4 dark:bg-surface-600 dark:after:bg-surface-200"
					></div>
				</label>
			</div>

			<!-- Certificate Expiring -->
			<div class="flex items-center justify-between py-4 first:pt-0 last:pb-0">
				<div>
					<p class="font-medium text-surface-900 dark:text-surface-100">
						Certificate Expiring
					</p>
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Get notified when a machine certificate is about to expire.
					</p>
				</div>
				<label class="relative inline-flex cursor-pointer items-center">
					<input
						type="checkbox"
						bind:checked={certificateExpiring}
						disabled
						class="peer sr-only"
					/>
					<div
						class="h-6 w-10 rounded-full bg-surface-300 after:absolute after:left-[2px] after:top-[2px] after:h-5 after:w-5 after:rounded-full after:bg-surface-50 after:transition-all after:content-[''] peer-checked:bg-primary-500 peer-checked:after:translate-x-4 dark:bg-surface-600 dark:after:bg-surface-200"
					></div>
				</label>
			</div>

			<!-- User Registration -->
			<div class="flex items-center justify-between py-4 first:pt-0 last:pb-0">
				<div>
					<p class="font-medium text-surface-900 dark:text-surface-100">
						User Registration
					</p>
					<p class="text-sm text-surface-500 dark:text-surface-400">
						Get notified when a new user registers an account.
					</p>
				</div>
				<label class="relative inline-flex cursor-pointer items-center">
					<input
						type="checkbox"
						bind:checked={userRegistration}
						disabled
						class="peer sr-only"
					/>
					<div
						class="h-6 w-10 rounded-full bg-surface-300 after:absolute after:left-[2px] after:top-[2px] after:h-5 after:w-5 after:rounded-full after:bg-surface-50 after:transition-all after:content-[''] peer-checked:bg-primary-500 peer-checked:after:translate-x-4 dark:bg-surface-600 dark:after:bg-surface-200"
					></div>
				</label>
			</div>
		</div>
	</div>

	<!-- Note -->
	<div class="flex items-start gap-2 rounded-lg bg-surface-50 p-4 dark:bg-surface-900">
		<Info class="mt-0.5 h-4 w-4 shrink-0 text-surface-400" />
		<p class="text-sm text-surface-500 dark:text-surface-400">
			Notification delivery is coming in a future update.
		</p>
	</div>
</div>
