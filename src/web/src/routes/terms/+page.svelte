<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';
	import { page } from '$app/state';
</script>

<svelte:head>
	<title>Terms of Service — Vord</title>
</svelte:head>

<PublicShell user={page.data.user ?? null}>
	<div class="mx-auto max-w-3xl px-6 py-16">
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Terms of Service</h1>
		<p class="mt-2 text-sm text-surface-500">Effective date: March 28, 2026</p>

		<div class="mt-10 space-y-8 text-surface-700 dark:text-surface-300">
			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">1. Acceptance of Terms</h2>
				<p class="mt-2 leading-relaxed">
					By accessing or using Vord (the "Service"), operated by Framlux LLC ("we", "us", or "our"),
					you agree to be bound by these Terms of Service. If you do not agree, do not use the Service.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">2. Description of Service</h2>
				<p class="mt-2 leading-relaxed">
					Vord is a fleet management platform that provides insight into Linux-based servers.
					The Service includes a web dashboard, a server-side API, and an installable agent that
					runs on your machines to collect and transmit system telemetry.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">3. Accounts and Tenants</h2>
				<p class="mt-2 leading-relaxed">
					You authenticate via a third-party identity provider (GitHub, Google, Microsoft, or your
					organization's SSO). You are responsible for maintaining the security of your account and
					for all activity under your tenant. You must promptly notify us of any unauthorized access.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">4. Agent Installation</h2>
				<p class="mt-2 leading-relaxed">
					By installing the Vord agent on a machine, you represent that you have the authority to do so.
					The agent runs with root privileges to collect system telemetry and communicates with our
					servers over encrypted channels. Each machine is assigned a unique API key upon registration.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">5. API Keys and Security</h2>
				<p class="mt-2 leading-relaxed">
					You are responsible for safeguarding all API keys and registration tokens issued to your
					tenant. Do not share API keys publicly or embed them in client-side code. Compromised keys
					should be revoked immediately through the dashboard.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">6. Acceptable Use</h2>
				<p class="mt-2 leading-relaxed">
					You agree not to misuse the Service. This includes but is not limited to: interfering with
					service infrastructure, reverse-engineering the agent or API, exceeding your subscription
					tier's machine limits through circumvention, or using the Service for any unlawful purpose.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">7. Subscriptions and Billing</h2>
				<p class="mt-2 leading-relaxed">
					The Service offers Free, Pro, and Team tiers with varying machine limits and features.
					Paid subscriptions are billed through Stripe. You may upgrade or downgrade at any time.
					Refunds are handled on a case-by-case basis. We reserve the right to change pricing
					with 30 days' notice.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">8. Data and Telemetry</h2>
				<p class="mt-2 leading-relaxed">
					The agent collects system telemetry from your machines (hardware info, OS details, resource
					usage, installed packages) and transmits it to our servers. Telemetry is only accepted from
					machines with a valid API key tied to an active subscription. See our
					<a href="/privacy" class="text-primary-500 hover:underline">Privacy Policy</a>
					for details on how we handle your data.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">9. Termination</h2>
				<p class="mt-2 leading-relaxed">
					You may stop using the Service at any time by uninstalling agents and canceling your
					subscription. We may suspend or terminate access for violations of these terms. Upon
					termination, your right to use the Service ceases and we may delete your data after a
					reasonable retention period.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">10. Limitation of Liability</h2>
				<p class="mt-2 leading-relaxed">
					The Service is provided "as is" without warranties of any kind. Framlux LLC shall not be
					liable for any indirect, incidental, or consequential damages arising from your use of the
					Service. Our total liability is limited to the amount you paid us in the 12 months preceding
					the claim.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">11. Changes to Terms</h2>
				<p class="mt-2 leading-relaxed">
					We may update these terms from time to time. We will notify you of material changes via
					email or through the Service. Continued use after changes take effect constitutes acceptance
					of the updated terms.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">12. Governing Law</h2>
				<p class="mt-2 leading-relaxed">
					These terms are governed by the laws of the State of Washington, United States, without
					regard to conflict of law principles.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">13. Contact</h2>
				<p class="mt-2 leading-relaxed">
					Questions about these terms? Contact us at
					<a href="mailto:legal@framlux.com" class="text-primary-500 hover:underline">legal@framlux.com</a>.
				</p>
			</section>
		</div>
	</div>
</PublicShell>
