<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import PublicShell from '$lib/components/PublicShell.svelte';
	import { page } from '$app/state';
</script>

<svelte:head>
	<title>Privacy Policy — Vord</title>
</svelte:head>

<PublicShell user={page.data.user ?? null}>
	<div class="mx-auto max-w-3xl px-6 py-16">
		<h1 class="text-3xl font-bold text-surface-900 dark:text-surface-50">Privacy Policy</h1>
		<p class="mt-2 text-sm text-surface-500">Effective date: March 28, 2026</p>

		<div class="mt-8 rounded-lg border border-primary-200 bg-primary-50 p-4 dark:border-primary-800 dark:bg-primary-900/20">
			<p class="font-medium text-primary-700 dark:text-primary-300">
				We will never sell your data. Not your account information, not your machine telemetry,
				not your usage patterns — no exceptions, no loopholes, no "de-identified data" carve-outs.
				Your data is yours.
			</p>
		</div>

		<div class="mt-10 space-y-8 text-surface-700 dark:text-surface-300">
			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">1. Who We Are</h2>
				<p class="mt-2 leading-relaxed">
					Vord is operated by Framlux LLC. This policy describes how we collect, use, and protect
					your information when you use the Vord fleet management platform.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">2. Information We Collect</h2>

				<h3 class="mt-4 font-medium text-surface-800 dark:text-surface-200">Account Information</h3>
				<p class="mt-1 leading-relaxed">
					When you sign in, we receive basic profile information from your identity provider
					(GitHub, Google, Microsoft, or your organization's OIDC provider): your name, email
					address, and profile identifier. We do not store passwords or passkeys.
				</p>

				<h3 class="mt-4 font-medium text-surface-800 dark:text-surface-200">Machine Telemetry</h3>
				<p class="mt-1 leading-relaxed">
					The Vord agent collects system telemetry from machines where it is installed, including
					hardware specifications, operating system details, resource utilization, and installed
					packages. This data is transmitted over encrypted channels and stored in association with
					your tenant.
				</p>

				<h3 class="mt-4 font-medium text-surface-800 dark:text-surface-200">Billing Information</h3>
				<p class="mt-1 leading-relaxed">
					Payment processing is handled by Stripe. We do not store credit card numbers or bank
					account details. We retain Stripe customer and subscription identifiers to manage your
					account.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">3. How We Use Your Information</h2>
				<p class="mt-2 leading-relaxed">We use your information to:</p>
				<ul class="mt-2 list-inside list-disc space-y-1 leading-relaxed">
					<li>Provide and operate the Service</li>
					<li>Display fleet dashboards and machine insights</li>
					<li>Process billing and manage subscriptions</li>
					<li>Send service-related notifications (security alerts, billing updates)</li>
					<li>Diagnose and fix technical issues</li>
				</ul>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">4. What We Never Do</h2>
				<p class="mt-2 leading-relaxed">
					We will never sell, rent, or trade your personal information or machine telemetry to third
					parties for any reason. We do not use your data for advertising. We do not create
					"de-identified" or "aggregated" datasets from your data for sale.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">5. Third-Party Services</h2>
				<p class="mt-2 leading-relaxed">
					We share limited information with the following services solely to operate the platform:
				</p>
				<ul class="mt-2 list-inside list-disc space-y-1 leading-relaxed">
					<li><strong>Identity providers</strong> (GitHub, Google, Microsoft) — for authentication only</li>
					<li><strong>Stripe</strong> — for payment processing only</li>
				</ul>
				<p class="mt-2 leading-relaxed">
					These providers have their own privacy policies governing their use of your data.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">6. Cookies</h2>
				<p class="mt-2 leading-relaxed">
					We use minimal cookies for essential functionality: session authentication and your theme
					preference (<code class="rounded bg-surface-100 px-1 text-sm dark:bg-surface-700">framlux_theme</code>).
					We do not use tracking cookies, analytics cookies, or third-party advertising cookies.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">7. Data Retention</h2>
				<p class="mt-2 leading-relaxed">
					We retain your data for as long as your account is active. Machine telemetry is retained
					according to your subscription tier. If you cancel your account, we will delete your data
					within a reasonable period, except where retention is required by law.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">8. Data Security</h2>
				<p class="mt-2 leading-relaxed">
					We protect your data using encryption in transit and at rest, hashed API keys, and
					role-based access controls. The Vord agent communicates exclusively over encrypted gRPC
					channels. While no system is perfectly secure, we take reasonable measures to protect
					your information.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">9. Your Rights</h2>
				<p class="mt-2 leading-relaxed">
					You may request access to, correction of, or deletion of your personal information at any
					time by contacting us. You can remove machines from the platform by uninstalling the agent
					and revoking their API keys.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">10. Changes to This Policy</h2>
				<p class="mt-2 leading-relaxed">
					We may update this policy from time to time. We will notify you of material changes via
					email or through the Service. The effective date at the top of this page indicates when it
					was last revised.
				</p>
			</section>

			<section>
				<h2 class="text-lg font-semibold text-surface-900 dark:text-surface-50">11. Contact</h2>
				<p class="mt-2 leading-relaxed">
					Questions about this policy? Contact us at
					<a href="mailto:legal@framlux.com" class="text-primary-500 hover:underline">legal@framlux.com</a>.
				</p>
			</section>
		</div>
	</div>
</PublicShell>
