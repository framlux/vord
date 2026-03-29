<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import type { SubscriptionDto, UpcomingInvoiceDto, InvoiceDto, UsagePointDto } from '$lib/api/types';
	import { CreditCard, ArrowUpCircle, ArrowDownCircle, ExternalLink, AlertCircle, XCircle, RotateCcw, Calculator, Receipt, TrendingUp, Download, ChevronDown } from 'lucide-svelte';

	let { data, form } = $props();

	const subscription: SubscriptionDto | null = $derived(data.subscription);

	const isCanceled = $derived(subscription !== null && subscription.status === 'Canceled');
	const isFree = $derived((subscription === null || subscription.tier === 'Free') && (isCanceled === false));
	const isPaid = $derived(subscription !== null && (subscription.tier === 'Pro' || subscription.tier === 'Team') && (isCanceled === false));
	const isPro = $derived(subscription !== null && subscription.tier === 'Pro');
	const isTeam = $derived(subscription !== null && subscription.tier === 'Team');
	const isPastDue = $derived(subscription !== null && subscription.status === 'PastDue');
	const hasPendingAction = $derived(subscription !== null && subscription.cancelAtPeriodEnd);
	const pendingAction = $derived(subscription?.pendingAction ?? null);
	const isDowngrading = $derived(pendingAction === 'DowngradeToFree' || pendingAction === 'DowngradeToPro');
	const isCanceling = $derived(pendingAction === 'CancelAccount');

	const hasBillingService = $derived(data.billingEnabled === true);

	const upcomingInvoice: UpcomingInvoiceDto | null = $derived(data.upcomingInvoice ?? null);
	const invoices: InvoiceDto[] = $derived(data.invoices ?? []);
	const usageHistory: UsagePointDto[] = $derived(data.usageHistory ?? []);

	let showLineItems = $state(false);

	function formatCents(cents: number, currency: string = 'usd'): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(cents / 100);
	}

	function formatShortDate(dateStr: string): string {
		return new Date(dateStr).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
	}

	function formatMonth(monthStr: string): string {
		const [year, month] = monthStr.split('-');
		return new Date(parseInt(year), parseInt(month) - 1).toLocaleDateString('en-US', { month: 'short', year: 'numeric' });
	}

	const maxUsageCount = $derived(Math.max(1, ...usageHistory.map(p => p.machineCount)));

	let showCancelConfirm = $state(false);
	let showDowngradeToFreeConfirm = $state(false);
	let showDowngradeToProConfirm = $state(false);
	let machineCount = $state(1);

	function getTierBadgeClasses(tier: string): string {
		if (tier === 'Pro') {
			return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';
		}
		if (tier === 'Team') {
			return 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400';
		}

		return 'bg-surface-100 text-surface-700 dark:bg-surface-700 dark:text-surface-300';
	}

	function getStatusBadgeClasses(status: string): string {
		if (status === 'Active') {
			return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400';
		}
		if (status === 'PastDue') {
			return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400';
		}
		if (status === 'Canceled') {
			return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
		}

		return 'bg-surface-100 text-surface-700 dark:bg-surface-700 dark:text-surface-300';
	}

	function formatDisplayStatus(sub: SubscriptionDto): string {
		if (sub.cancelAtPeriodEnd) {
			if (sub.pendingAction === 'CancelAccount') {
				return 'Canceling';
			}
			if (sub.pendingAction === 'DowngradeToFree') {
				return 'Downgrading to Free';
			}
			if (sub.pendingAction === 'DowngradeToPro') {
				return 'Downgrading to Pro';
			}

			return 'Canceling';
		}
		if (sub.status === 'PastDue') {
			return 'Past Due';
		}

		return sub.status;
	}

	function getDisplayStatusBadgeClasses(sub: SubscriptionDto): string {
		if (sub.cancelAtPeriodEnd) {
			if (sub.pendingAction === 'CancelAccount') {
				return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400';
			}

			return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400';
		}

		return getStatusBadgeClasses(sub.status);
	}

	function formatPeriodEnd(dateStr: string): string {
		return new Date(dateStr).toLocaleDateString('en-US', {
			month: 'long',
			day: 'numeric',
			year: 'numeric'
		});
	}

	function getMachineLimitText(sub: SubscriptionDto): string {
		if (sub.machineLimit === null) {
			return 'Unlimited';
		}

		return `${sub.machineCount} / ${sub.machineLimit}`;
	}

	function getMachineLimitPercent(sub: SubscriptionDto): number {
		if (sub.machineLimit === null || sub.machineLimit === 0) {
			return 0;
		}

		return Math.min(100, Math.round((sub.machineCount / sub.machineLimit) * 100));
	}

	function getPendingActionDescription(): string {
		if (pendingAction === 'CancelAccount') {
			return 'Your account will be canceled';
		}
		if (pendingAction === 'DowngradeToFree') {
			return 'Your subscription will be downgraded to the Free tier';
		}
		if (pendingAction === 'DowngradeToPro') {
			return 'Your subscription will be downgraded to the Pro tier';
		}

		return 'Your subscription will be canceled';
	}
</script>

<div class="space-y-8">
	<!-- Page Header -->
	<div>
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Billing</h1>
		<p class="mt-1 text-surface-500 dark:text-surface-400">
			Manage your subscription and billing details.
		</p>
	</div>

	<!-- Form result feedback -->
	{#if form}
		{#if form.success}
			<div class="rounded-xl border border-green-300 bg-green-50 p-4 dark:border-green-700 dark:bg-green-900/20">
				<p class="text-sm font-medium text-green-800 dark:text-green-200">{form.message}</p>
			</div>
		{:else if form.message}
			<div class="rounded-xl border border-red-300 bg-red-50 p-4 dark:border-red-700 dark:bg-red-900/20">
				<p class="text-sm font-medium text-red-800 dark:text-red-200">{form.message}</p>
			</div>
		{/if}
	{/if}

	<!-- Past Due Warning Banner -->
	{#if isPastDue}
		<div class="rounded-xl border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-900/20">
			<div class="flex items-start gap-3">
				<AlertCircle class="mt-0.5 h-5 w-5 text-amber-600 dark:text-amber-400" />
				<div class="flex-1">
					<h3 class="font-semibold text-amber-800 dark:text-amber-200">Payment Past Due</h3>
					<p class="mt-1 text-sm text-amber-700 dark:text-amber-300">
						Your payment is past due. Please update your payment method to avoid service interruption.
					</p>
					{#if hasBillingService}
						<form method="POST" action="?/portal" class="mt-3">
							<button
								type="submit"
								class="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-amber-700"
							>
								Update Payment Method
							</button>
						</form>
					{/if}
				</div>
			</div>
		</div>
	{/if}

	<!-- Pending Action Banner (cancel or downgrade) -->
	{#if hasPendingAction && subscription !== null}
		<div class="rounded-xl border p-4 {isCanceling ? 'border-red-300 bg-red-50 dark:border-red-700 dark:bg-red-900/20' : 'border-amber-300 bg-amber-50 dark:border-amber-700 dark:bg-amber-900/20'}">
			<div class="flex items-start gap-3">
				{#if isCanceling}
					<XCircle class="mt-0.5 h-5 w-5 text-red-600 dark:text-red-400" />
				{:else}
					<ArrowDownCircle class="mt-0.5 h-5 w-5 text-amber-600 dark:text-amber-400" />
				{/if}
				<div class="flex-1">
					<h3 class="font-semibold {isCanceling ? 'text-red-800 dark:text-red-200' : 'text-amber-800 dark:text-amber-200'}">
						{isCanceling ? 'Account Cancellation Pending' : 'Downgrade Pending'}
					</h3>
					<p class="mt-1 text-sm {isCanceling ? 'text-red-700 dark:text-red-300' : 'text-amber-700 dark:text-amber-300'}">
						{getPendingActionDescription()}
						{#if subscription.currentPeriodEnd !== null}
							on {formatPeriodEnd(subscription.currentPeriodEnd)}.
						{:else}
							at the end of the current billing period.
						{/if}
						You'll retain access to {subscription.tier} features until then.
					</p>
					<form method="POST" action="?/resume" class="mt-3">
						<button
							type="submit"
							class="inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium text-white transition-colors {isCanceling ? 'bg-red-600 hover:bg-red-700' : 'bg-amber-600 hover:bg-amber-700'}"
						>
							<RotateCcw class="h-4 w-4" />
							{isCanceling ? 'Undo Cancellation' : 'Undo Downgrade'}
						</button>
					</form>
				</div>
			</div>
		</div>
	{/if}

	<!-- Canceled Account Banner -->
	{#if isCanceled}
		<div class="rounded-xl border border-red-300 bg-red-50 p-4 dark:border-red-700 dark:bg-red-900/20">
			<div class="flex items-start gap-3">
				<XCircle class="mt-0.5 h-5 w-5 text-red-600 dark:text-red-400" />
				<div class="flex-1">
					<h3 class="font-semibold text-red-800 dark:text-red-200">Your account is canceled</h3>
					<p class="mt-1 text-sm text-red-700 dark:text-red-300">
						Your subscription has been canceled and your account is in read-only mode.
						Reactivate to regain full access.
					</p>
					{#if hasBillingService}
						<div class="mt-4 flex flex-wrap gap-3">
							<form method="POST" action="?/reactivate">
								<button
									type="submit"
									class="inline-flex items-center gap-2 rounded-lg bg-green-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-green-700 dark:bg-green-500 dark:hover:bg-green-600"
								>
									<RotateCcw class="h-4 w-4" />
									Reactivate (Free)
								</button>
							</form>
							<form method="POST" action="?/checkout">
								<input type="hidden" name="tier" value="pro" />
								<button
									type="submit"
									class="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600"
								>
									<ArrowUpCircle class="h-4 w-4" />
									Reactivate with Pro
								</button>
							</form>
							<form method="POST" action="?/checkout">
								<input type="hidden" name="tier" value="team" />
								<button
									type="submit"
									class="inline-flex items-center gap-2 rounded-lg bg-purple-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-purple-700 dark:bg-purple-500 dark:hover:bg-purple-600"
								>
									<ArrowUpCircle class="h-4 w-4" />
									Reactivate with Team
								</button>
							</form>
						</div>
					{/if}
				</div>
			</div>
		</div>
	{/if}

	<!-- Current Plan -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="mb-6 flex items-center gap-3">
			<CreditCard class="h-5 w-5 text-surface-400 dark:text-surface-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Current Plan</h2>
		</div>

		{#if subscription === null}
			<!-- Free tier with no subscription record -->
			<div class="space-y-4">
				<div class="flex items-center gap-4">
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Tier</p>
						<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getTierBadgeClasses('Free')}">
							Free
						</span>
					</div>
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Machines</p>
						<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">0 / 3</p>
					</div>
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Data Retention</p>
						<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">1 day(s)</p>
					</div>
				</div>
			</div>
		{:else}
			<div class="space-y-4">
				<div class="flex flex-wrap items-center gap-6">
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Tier</p>
						<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getTierBadgeClasses(subscription.tier)}">
							{subscription.tier}
						</span>
					</div>
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Status</p>
						<span class="mt-1 inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium {getDisplayStatusBadgeClasses(subscription)}">
							{formatDisplayStatus(subscription)}
						</span>
					</div>
					<div>
						<p class="text-xs text-surface-500 dark:text-surface-400">Data Retention</p>
						<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
							{subscription.retentionDays} day(s)
						</p>
					</div>
					{#if subscription.currentPeriodEnd !== null}
						<div>
							<p class="text-xs text-surface-500 dark:text-surface-400">Current Period Ends</p>
							<p class="mt-1 text-sm font-medium text-surface-900 dark:text-surface-100">
								{formatPeriodEnd(subscription.currentPeriodEnd)}
							</p>
						</div>
					{/if}
				</div>

				<!-- Machine usage progress bar -->
				<div>
					<div class="mb-1 flex items-center justify-between">
						<p class="text-xs text-surface-500 dark:text-surface-400">Machine Usage</p>
						<p class="text-xs font-medium text-surface-700 dark:text-surface-300">
							{getMachineLimitText(subscription)}
						</p>
					</div>
					{#if subscription.machineLimit !== null}
						<div class="h-2 w-full rounded-full bg-surface-200 dark:bg-surface-700">
							<div
								class="h-2 rounded-full transition-all {getMachineLimitPercent(subscription) >= 90 ? 'bg-red-500' : getMachineLimitPercent(subscription) >= 70 ? 'bg-amber-500' : 'bg-primary-500'}"
								style="width: {getMachineLimitPercent(subscription)}%"
							></div>
						</div>
					{:else}
						<div class="h-2 w-full rounded-full bg-surface-200 dark:bg-surface-700">
							<div class="h-2 rounded-full bg-primary-500" style="width: 5%"></div>
						</div>
					{/if}
				</div>
			</div>
		{/if}
	</div>

	<!-- Upgrade / Manage / Downgrade Section -->
	{#if hasBillingService}
		<div
			class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
		>
			{#if isFree}
				<!-- Free tier: upgrade options + cancel -->
				<div class="space-y-6">
					<div class="flex items-start gap-4">
						<div class="rounded-lg bg-blue-100 p-3 dark:bg-blue-900/30">
							<ArrowUpCircle class="h-6 w-6 text-blue-600 dark:text-blue-400" />
						</div>
						<div class="flex-1">
							<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
								Upgrade Your Plan
							</h3>
							<p class="mt-1 text-sm text-surface-500 dark:text-surface-400">
								Unlock unlimited machines, extended data retention, alerting, and more.
							</p>
							<div class="mt-4 flex flex-wrap gap-3">
								<form method="POST" action="?/checkout">
									<input type="hidden" name="tier" value="pro" />
									<button
										type="submit"
										class="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-blue-700 dark:bg-blue-500 dark:hover:bg-blue-600"
									>
										<ArrowUpCircle class="h-4 w-4" />
										Upgrade to Pro — $3/host/mo
									</button>
								</form>
								<form method="POST" action="?/checkout">
									<input type="hidden" name="tier" value="team" />
									<button
										type="submit"
										class="inline-flex items-center gap-2 rounded-lg bg-purple-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-purple-700 dark:bg-purple-500 dark:hover:bg-purple-600"
									>
										<ArrowUpCircle class="h-4 w-4" />
										Upgrade to Team — $5/host/mo
									</button>
								</form>
							</div>
						</div>
					</div>

					<!-- Cancel Account (Free tier: immediate) -->
					{#if hasPendingAction === false}
						<div class="border-t border-surface-200 pt-6 dark:border-surface-700">
							{#if showCancelConfirm === false}
								<button
									type="button"
									onclick={() => showCancelConfirm = true}
									class="text-sm text-red-600 transition-colors hover:text-red-700 dark:text-red-400 dark:hover:text-red-300"
								>
									Cancel Account
								</button>
							{:else}
								<div class="rounded-lg border border-red-200 bg-red-50 p-4 dark:border-red-800 dark:bg-red-900/20">
									<h4 class="font-semibold text-red-800 dark:text-red-200">
										Are you sure you want to cancel your account?
									</h4>
									<p class="mt-2 text-sm text-red-700 dark:text-red-300">
										Your account will be deactivated immediately. You will need to re-subscribe to regain access.
									</p>
									<div class="mt-4 flex gap-3">
										<form method="POST" action="?/cancel">
											<button
												type="submit"
												class="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-red-700"
											>
												Confirm Cancellation
											</button>
										</form>
										<button
											type="button"
											onclick={() => showCancelConfirm = false}
											class="rounded-lg bg-surface-200 px-4 py-2 text-sm font-medium text-surface-700 transition-colors hover:bg-surface-300 dark:bg-surface-700 dark:text-surface-300 dark:hover:bg-surface-600"
										>
											Keep My Account
										</button>
									</div>
								</div>
							{/if}
						</div>
					{/if}
				</div>
			{:else if isPaid}
				<div class="space-y-6">
					<!-- Manage Subscription + Upgrade -->
					<div class="flex items-start gap-4">
						<div class="rounded-lg bg-surface-100 p-3 dark:bg-surface-700">
							<ExternalLink class="h-6 w-6 text-surface-600 dark:text-surface-400" />
						</div>
						<div class="flex-1">
							<h3 class="text-lg font-semibold text-surface-900 dark:text-surface-50">
								Manage Subscription
							</h3>
							<p class="mt-1 text-sm text-surface-500 dark:text-surface-400">
								Update payment methods, view invoices, and manage your subscription through the billing portal.
							</p>
							<div class="mt-4 flex flex-wrap gap-3">
								<form method="POST" action="?/portal">
									<button
										type="submit"
										class="inline-flex items-center gap-2 rounded-lg bg-surface-800 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-surface-700 dark:bg-surface-600 dark:hover:bg-surface-500"
									>
										<ExternalLink class="h-4 w-4" />
										Manage Subscription
									</button>
								</form>
								{#if isPro}
									<form method="POST" action="?/checkout">
										<input type="hidden" name="tier" value="team" />
										<button
											type="submit"
											class="inline-flex items-center gap-2 rounded-lg bg-purple-600 px-5 py-2.5 text-sm font-medium text-white transition-colors hover:bg-purple-700 dark:bg-purple-500 dark:hover:bg-purple-600"
										>
											<ArrowUpCircle class="h-4 w-4" />
											Upgrade to Team
										</button>
									</form>
								{/if}
							</div>
						</div>
					</div>

					<!-- Downgrade and Cancel actions (only when no pending action) -->
					{#if hasPendingAction === false}
						<div class="border-t border-surface-200 pt-6 dark:border-surface-700">
							<h4 class="mb-4 text-sm font-medium text-surface-700 dark:text-surface-300">Change Plan</h4>
							<div class="space-y-4">
								<!-- Team tier: Downgrade to Pro -->
								{#if isTeam}
									{#if showDowngradeToProConfirm === false}
										<button
											type="button"
											onclick={() => showDowngradeToProConfirm = true}
											class="inline-flex items-center gap-2 text-sm text-amber-600 transition-colors hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300"
										>
											<ArrowDownCircle class="h-4 w-4" />
											Downgrade to Pro
										</button>
									{:else}
										<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/20">
											<h4 class="font-semibold text-amber-800 dark:text-amber-200">
												Downgrade to Pro?
											</h4>
											<p class="mt-2 text-sm text-amber-700 dark:text-amber-300">
												This takes effect immediately with prorated billing. The following changes will apply:
											</p>
											<ul class="mt-2 list-inside list-disc text-sm text-amber-700 dark:text-amber-300">
												<li>Custom OIDC SSO will be disabled</li>
												<li>Custom alert rules will be disabled (default rules remain)</li>
												<li>Audit log access will be removed</li>
												<li>Data retention will be reduced to 30 days</li>
											</ul>
											<div class="mt-4 flex gap-3">
												<form method="POST" action="?/downgrade">
													<input type="hidden" name="targetTier" value="pro" />
													<button
														type="submit"
														class="rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-amber-700"
													>
														Confirm Downgrade to Pro
													</button>
												</form>
												<button
													type="button"
													onclick={() => showDowngradeToProConfirm = false}
													class="rounded-lg bg-surface-200 px-4 py-2 text-sm font-medium text-surface-700 transition-colors hover:bg-surface-300 dark:bg-surface-700 dark:text-surface-300 dark:hover:bg-surface-600"
												>
													Keep Team
												</button>
											</div>
										</div>
									{/if}
								{/if}

								<!-- Team and Pro: Downgrade to Free -->
								{#if showDowngradeToFreeConfirm === false}
									<button
										type="button"
										onclick={() => showDowngradeToFreeConfirm = true}
										class="inline-flex items-center gap-2 text-sm text-amber-600 transition-colors hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300"
									>
										<ArrowDownCircle class="h-4 w-4" />
										Downgrade to Free
									</button>
								{:else}
									<div class="rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-800 dark:bg-amber-900/20">
										<h4 class="font-semibold text-amber-800 dark:text-amber-200">
											Downgrade to Free?
										</h4>
										<p class="mt-2 text-sm text-amber-700 dark:text-amber-300">
											This takes effect at the end of your current billing period
											{#if subscription !== null && subscription.currentPeriodEnd !== null}
												on {formatPeriodEnd(subscription.currentPeriodEnd)}
											{/if}.
											The following changes will apply:
										</p>
										<ul class="mt-2 list-inside list-disc text-sm text-amber-700 dark:text-amber-300">
											<li>Machine limit will be reduced to 3</li>
											<li>Data retention will be reduced to 1 day</li>
											<li>All alerting will be disabled</li>
											{#if isTeam}
												<li>Custom OIDC SSO will be disabled</li>
												<li>Audit log access will be removed</li>
											{/if}
											<li>Webhook notification endpoints will be disabled</li>
										</ul>
										<div class="mt-4 flex gap-3">
											<form method="POST" action="?/downgrade">
												<input type="hidden" name="targetTier" value="free" />
												<button
													type="submit"
													class="rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-amber-700"
												>
													Confirm Downgrade to Free
												</button>
											</form>
											<button
												type="button"
												onclick={() => showDowngradeToFreeConfirm = false}
												class="rounded-lg bg-surface-200 px-4 py-2 text-sm font-medium text-surface-700 transition-colors hover:bg-surface-300 dark:bg-surface-700 dark:text-surface-300 dark:hover:bg-surface-600"
											>
												Keep Current Plan
											</button>
										</div>
									</div>
								{/if}

								<!-- Cancel Account (danger zone) -->
								<div class="mt-4 border-t border-surface-200 pt-4 dark:border-surface-700">
									{#if showCancelConfirm === false}
										<button
											type="button"
											onclick={() => showCancelConfirm = true}
											class="text-sm text-red-600 transition-colors hover:text-red-700 dark:text-red-400 dark:hover:text-red-300"
										>
											Cancel Account
										</button>
									{:else}
										<div class="rounded-lg border border-red-200 bg-red-50 p-4 dark:border-red-800 dark:bg-red-900/20">
											<h4 class="font-semibold text-red-800 dark:text-red-200">
												Are you sure you want to cancel your account?
											</h4>
											<p class="mt-2 text-sm text-red-700 dark:text-red-300">
												Your account will lose ALL service at the end of the current billing period
												{#if subscription !== null && subscription.currentPeriodEnd !== null}
													on {formatPeriodEnd(subscription.currentPeriodEnd)}
												{/if}.
												You will need to re-subscribe to regain access. This is different from downgrading
												— cancellation stops all service entirely.
											</p>
											<div class="mt-4 flex gap-3">
												<form method="POST" action="?/cancel">
													<button
														type="submit"
														class="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-red-700"
													>
														Confirm Cancellation
													</button>
												</form>
												<button
													type="button"
													onclick={() => showCancelConfirm = false}
													class="rounded-lg bg-surface-200 px-4 py-2 text-sm font-medium text-surface-700 transition-colors hover:bg-surface-300 dark:bg-surface-700 dark:text-surface-300 dark:hover:bg-surface-600"
												>
													Keep My Account
												</button>
											</div>
										</div>
									{/if}
								</div>
							</div>
						</div>
					{/if}
				</div>
			{/if}
		</div>
	{/if}

	<!-- Current Bill Summary -->
	{#if upcomingInvoice?.hasInvoice}
		<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
			<div class="flex items-center gap-2 text-lg font-semibold text-surface-900 dark:text-surface-50">
				<Receipt size={20} />
				Current Period
			</div>

			<div class="mt-4 flex flex-wrap items-baseline gap-x-6 gap-y-2">
				<span class="text-3xl font-bold text-surface-900 dark:text-surface-50">
					{formatCents(upcomingInvoice.amountDueCents, upcomingInvoice.currency)}
				</span>
				{#if upcomingInvoice.unitAmountCents > 0}
					<span class="text-sm text-surface-500">
						{formatCents(upcomingInvoice.unitAmountCents, upcomingInvoice.currency)}/host/mo
					</span>
				{/if}
				{#if upcomingInvoice.nextPaymentAttempt}
					<span class="text-sm text-surface-500">
						Next charge: {formatShortDate(upcomingInvoice.nextPaymentAttempt)}
					</span>
				{/if}
			</div>

			{#if upcomingInvoice.lines.length > 0}
				<button
					onclick={() => showLineItems = !showLineItems}
					class="mt-3 flex items-center gap-1 text-sm text-primary-500 transition-colors hover:text-primary-600"
				>
					{showLineItems ? 'Hide' : 'Show'} breakdown
					<ChevronDown size={14} class="transition-transform {showLineItems ? 'rotate-180' : ''}" />
				</button>

				{#if showLineItems}
					<div class="mt-3 divide-y divide-surface-100 rounded-lg border border-surface-200 dark:divide-surface-700 dark:border-surface-700">
						{#each upcomingInvoice.lines as line}
							<div class="flex items-center justify-between px-4 py-2.5 text-sm">
								<div class="flex items-center gap-2">
									<span class="text-surface-700 dark:text-surface-300">{line.description}</span>
									{#if line.proration}
										<span class="rounded bg-amber-100 px-1.5 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-900/30 dark:text-amber-400">Prorated</span>
									{/if}
								</div>
								<span class="font-medium text-surface-900 dark:text-surface-50">{formatCents(line.amountCents, upcomingInvoice.currency)}</span>
							</div>
						{/each}
					</div>
				{/if}
			{/if}
		</div>
	{/if}

	<!-- Invoice History -->
	{#if invoices.length > 0}
		<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
			<div class="flex items-center gap-2 text-lg font-semibold text-surface-900 dark:text-surface-50">
				<Receipt size={20} />
				Invoice History
			</div>

			<div class="mt-4 overflow-x-auto">
				<table class="w-full text-left text-sm">
					<thead>
						<tr class="border-b border-surface-200 dark:border-surface-700">
							<th class="pb-3 pr-4 font-medium text-surface-500">Date</th>
							<th class="pb-3 pr-4 font-medium text-surface-500">Period</th>
							<th class="pb-3 pr-4 font-medium text-surface-500">Amount</th>
							<th class="pb-3 pr-4 font-medium text-surface-500">Status</th>
							<th class="pb-3 font-medium text-surface-500"></th>
						</tr>
					</thead>
					<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
						{#each invoices as invoice}
							<tr class="hover:bg-surface-100/50 dark:hover:bg-surface-800/50">
								<td class="py-3 pr-4 text-surface-700 dark:text-surface-300">
									{formatShortDate(invoice.created)}
								</td>
								<td class="py-3 pr-4 text-surface-500">
									{#if invoice.periodStart && invoice.periodEnd}
										{formatShortDate(invoice.periodStart)} – {formatShortDate(invoice.periodEnd)}
									{:else}
										—
									{/if}
								</td>
								<td class="py-3 pr-4 font-medium text-surface-900 dark:text-surface-50">
									{formatCents(invoice.amountCents, invoice.currency)}
								</td>
								<td class="py-3 pr-4">
									<span class="inline-flex items-center rounded-full bg-green-100 px-2.5 py-0.5 text-xs font-medium text-green-800 dark:bg-green-900/30 dark:text-green-400">
										{invoice.status}
									</span>
								</td>
								<td class="py-3 text-right">
									{#if invoice.invoicePdfUrl}
										<a
											href={invoice.invoicePdfUrl}
											target="_blank"
											rel="noopener noreferrer"
											class="inline-flex items-center gap-1 text-sm text-primary-500 transition-colors hover:text-primary-600"
										>
											<Download size={14} />
											PDF
										</a>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/if}

	<!-- Usage & Cost Trend -->
	{#if usageHistory.length > 0 && usageHistory.some(p => p.machineCount > 0 || p.invoiceAmountCents > 0)}
		<div class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800">
			<div class="flex items-center gap-2 text-lg font-semibold text-surface-900 dark:text-surface-50">
				<TrendingUp size={20} />
				Usage Trend
			</div>

			<div class="mt-6 space-y-3">
				{#each usageHistory as point}
					<div class="flex items-center gap-4">
						<span class="w-20 shrink-0 text-sm text-surface-500">{formatMonth(point.month)}</span>
						<div class="flex flex-1 items-center gap-3">
							<div class="h-6 rounded bg-primary-500/20 dark:bg-primary-500/30 transition-all"
								style="width: {Math.max(4, (point.machineCount / maxUsageCount) * 100)}%">
								<div class="flex h-full items-center px-2 text-xs font-medium text-primary-700 dark:text-primary-300">
									{#if point.machineCount > 0}
										{point.machineCount} {point.machineCount === 1 ? 'machine' : 'machines'}
									{/if}
								</div>
							</div>
						</div>
						<span class="w-20 shrink-0 text-right text-sm font-medium text-surface-700 dark:text-surface-300">
							{point.invoiceAmountCents > 0 ? formatCents(point.invoiceAmountCents) : '—'}
						</span>
					</div>
				{/each}
			</div>

			<div class="mt-4 flex items-center justify-between text-xs text-surface-400">
				<span>Machines at month-end</span>
				<span>Invoice amount</span>
			</div>
		</div>
	{/if}

	<!-- Plan Comparison -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<h2 class="mb-4 text-lg font-semibold text-surface-900 dark:text-surface-50">Plan Comparison</h2>
		<div class="overflow-x-auto">
			<table class="w-full text-sm">
				<thead>
					<tr class="border-b border-surface-200 dark:border-surface-700">
						<th class="py-3 pr-4 text-left font-medium text-surface-500 dark:text-surface-400">Feature</th>
						<th class="px-4 py-3 text-center font-medium text-surface-500 dark:text-surface-400">Free</th>
						<th class="px-4 py-3 text-center font-medium text-blue-600 dark:text-blue-400">Pro</th>
						<th class="px-4 py-3 text-center font-medium text-purple-600 dark:text-purple-400">Team</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-100 dark:divide-surface-700">
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Machine Limit</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">3</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">Unlimited</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">Unlimited</td>
					</tr>
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Data Retention</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">1 day</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">30 days</td>
						<td class="px-4 py-3 text-center text-surface-600 dark:text-surface-400">365 days</td>
					</tr>
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Alerting</td>
						<td class="px-4 py-3 text-center text-surface-400">-</td>
						<td class="px-4 py-3 text-center text-green-600 dark:text-green-400">Default rules</td>
						<td class="px-4 py-3 text-center text-green-600 dark:text-green-400">Custom rules</td>
					</tr>
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Audit Log</td>
						<td class="px-4 py-3 text-center text-surface-400">-</td>
						<td class="px-4 py-3 text-center text-surface-400">-</td>
						<td class="px-4 py-3 text-center text-green-600 dark:text-green-400">Full access</td>
					</tr>
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Custom OIDC SSO</td>
						<td class="px-4 py-3 text-center text-surface-400">-</td>
						<td class="px-4 py-3 text-center text-surface-400">-</td>
						<td class="px-4 py-3 text-center text-green-600 dark:text-green-400">Included</td>
					</tr>
					<tr>
						<td class="py-3 pr-4 text-surface-900 dark:text-surface-100">Price</td>
						<td class="px-4 py-3 text-center font-medium text-surface-900 dark:text-surface-100">$0</td>
						<td class="px-4 py-3 text-center font-medium text-surface-900 dark:text-surface-100">$3/host/mo</td>
						<td class="px-4 py-3 text-center font-medium text-surface-900 dark:text-surface-100">$5/host/mo</td>
					</tr>
				</tbody>
			</table>
		</div>
	</div>

	<!-- Cost Calculator -->
	<div
		class="rounded-xl border border-surface-200 bg-surface-50 p-6 dark:border-surface-700 dark:bg-surface-800"
	>
		<div class="mb-4 flex items-center gap-3">
			<Calculator class="h-5 w-5 text-surface-400 dark:text-surface-500" />
			<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">Cost Calculator</h2>
		</div>
		<div class="space-y-4">
			<div>
				<label for="machine-count" class="block text-sm font-medium text-surface-700 dark:text-surface-300">
					Number of machines
				</label>
				<input
					id="machine-count"
					type="number"
					min="1"
					max="10000"
					bind:value={machineCount}
					class="mt-1 w-32 rounded-lg border border-surface-300 bg-surface-50 px-3 py-2 text-sm text-surface-900 dark:border-surface-600 dark:bg-surface-700 dark:text-surface-100"
				/>
			</div>
			<div class="flex gap-8">
				<div>
					<p class="text-sm text-surface-500 dark:text-surface-400">Pro</p>
					<p class="text-2xl font-bold text-blue-600 dark:text-blue-400">
						${machineCount * 3}<span class="text-sm font-normal">/mo</span>
					</p>
				</div>
				<div>
					<p class="text-sm text-surface-500 dark:text-surface-400">Team</p>
					<p class="text-2xl font-bold text-purple-600 dark:text-purple-400">
						${machineCount * 5}<span class="text-sm font-normal">/mo</span>
					</p>
				</div>
			</div>
			<p class="text-xs text-surface-400 dark:text-surface-500">
				Costs are prorated when adding or removing machines mid-cycle.
			</p>
		</div>
	</div>
</div>
