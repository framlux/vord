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
});
