// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import PausedHostBanner from './PausedHostBanner.svelte';

describe('PausedHostBanner', () => {
	it('names the host\'s place in line and how many older hosts must go', () => {
		render(PausedHostBanner, { props: { limit: 3, activeRank: 7, limitIsCustom: false, canManageBilling: true } });

		expect(screen.getByRole('status')).toHaveTextContent(
			'This host is paused. Your plan keeps your 3 oldest hosts active, and this one is #7. Remove 4 older hosts you no longer use, or upgrade, to resume it.'
		);
		expect(screen.getByRole('link', { name: 'Billing' })).toHaveAttribute('href', '/settings/billing');
	});

	it('uses the singular for one older host', () => {
		render(PausedHostBanner, { props: { limit: 3, activeRank: 4, limitIsCustom: false, canManageBilling: false } });

		expect(screen.getByRole('status')).toHaveTextContent('Remove 1 older host you no longer use, or upgrade, to resume it.');
		expect(screen.queryByRole('link', { name: 'Billing' })).not.toBeInTheDocument();
	});

	it('sends a tenant whose limit was set by agreement or support to support', () => {
		render(PausedHostBanner, { props: { limit: 0, activeRank: 1, limitIsCustom: true, canManageBilling: true } });

		expect(screen.getByRole('status')).toHaveTextContent('Contact support to change your host limit.');
		expect(screen.queryByText(/upgrade/)).not.toBeInTheDocument();
	});

	it('offers no billing link to a custom-limit tenant, since billing cannot change the limit', () => {
		render(PausedHostBanner, { props: { limit: 5, activeRank: 9, limitIsCustom: true, canManageBilling: true } });

		expect(screen.queryByRole('link', { name: 'Billing' })).not.toBeInTheDocument();
	});

	describe('when the figures are not usable', () => {
		it('says only that the host is paused when its rank is unknown', () => {
			// No machine detail gives a rank of 0, which would print "#0" and a negative count.
			render(PausedHostBanner, { props: { limit: 3, activeRank: 0, limitIsCustom: false, canManageBilling: true } });

			const banner = screen.getByRole('status');

			expect(banner).toHaveTextContent('This host is paused.');
			expect(banner).not.toHaveTextContent('#');
			expect(banner).not.toHaveTextContent('Remove');
			expect(screen.getByRole('link', { name: 'Billing' })).toHaveAttribute('href', '/settings/billing');
		});

		it('says only that the host is paused when the limit is stale and the rank is within it', () => {
			// A downgrade mid-session leaves the layout's limit behind the host's own rank, which would
			// print "Remove -993 older hosts".
			render(PausedHostBanner, { props: { limit: 1000, activeRank: 7, limitIsCustom: false, canManageBilling: true } });

			const banner = screen.getByRole('status');

			expect(banner).toHaveTextContent('This host is paused.');
			expect(banner).not.toHaveTextContent('Remove');
			expect(banner).not.toHaveTextContent('-');
			expect(screen.getByRole('link', { name: 'Billing' })).toBeInTheDocument();
		});

		it('says only that the host is paused when the rank equals the limit, leaving nothing to remove', () => {
			render(PausedHostBanner, { props: { limit: 3, activeRank: 3, limitIsCustom: false, canManageBilling: false } });

			const banner = screen.getByRole('status');

			expect(banner).toHaveTextContent('This host is paused.');
			expect(banner).not.toHaveTextContent('Remove');
			expect(screen.queryByRole('link', { name: 'Billing' })).not.toBeInTheDocument();
		});

		it('keeps a custom-limit tenant on the contact-support line and without a billing link', () => {
			render(PausedHostBanner, { props: { limit: 3, activeRank: 0, limitIsCustom: true, canManageBilling: true } });

			expect(screen.getByRole('status')).toHaveTextContent('This host is paused.');
			expect(screen.queryByRole('link', { name: 'Billing' })).not.toBeInTheDocument();
		});
	});

	describe('how it names the limit', () => {
		it('says the oldest host, not "1 oldest hosts", when the plan keeps one', () => {
			render(PausedHostBanner, { props: { limit: 1, activeRank: 4, limitIsCustom: false, canManageBilling: false } });

			expect(screen.getByRole('status')).toHaveTextContent(
				'This host is paused. Your plan keeps your oldest host active, and this one is #4. Remove 3 older hosts you no longer use, or upgrade, to resume it.'
			);
		});

		it('says the plan keeps no hosts active when the limit is zero', () => {
			render(PausedHostBanner, { props: { limit: 0, activeRank: 2, limitIsCustom: true, canManageBilling: false } });

			const banner = screen.getByRole('status');

			expect(banner).toHaveTextContent('This host is paused. Your plan keeps no hosts active.');
			expect(banner).toHaveTextContent('Contact support to change your host limit.');
			expect(banner).not.toHaveTextContent('0 oldest');
		});

		it('sends a zero-limit tenant to upgrade, since removing older hosts cannot resume this one', () => {
			render(PausedHostBanner, { props: { limit: 0, activeRank: 2, limitIsCustom: false, canManageBilling: true } });

			const banner = screen.getByRole('status');

			expect(banner).toHaveTextContent('This host is paused. Your plan keeps no hosts active. Upgrade to resume it.');
			expect(banner).not.toHaveTextContent('Remove');
			expect(screen.getByRole('link', { name: 'Billing' })).toBeInTheDocument();
		});

		it('keeps the exact wording for a limit above one', () => {
			render(PausedHostBanner, { props: { limit: 2, activeRank: 3, limitIsCustom: false, canManageBilling: false } });

			expect(screen.getByRole('status')).toHaveTextContent('Your plan keeps your 2 oldest hosts active, and this one is #3.');
		});
	});
});
