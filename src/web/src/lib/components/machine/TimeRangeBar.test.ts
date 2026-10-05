// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import TimeRangeBar from './TimeRangeBar.svelte';

function renderBar(retentionDays: number, tier?: string | null) {
	return render(TimeRangeBar, {
		props: { activeRange: '1h', retentionDays, tier, onrangechange: () => {} }
	});
}

describe('TimeRangeBar', () => {
	it('offers an upgrade for a range the retention does not cover', () => {
		renderBar(7, 'Pro');

		expect(screen.getByRole('button', { name: /30d/ }).getAttribute('title')).toBe(
			'Upgrade to Pro for 30d history'
		);
	});

	it('offers an upgrade when no tier is supplied', () => {
		renderBar(7);

		expect(screen.getByRole('button', { name: /30d/ }).getAttribute('title')).toBe(
			'Upgrade to Pro for 30d history'
		);
	});

	it('never tells an Enterprise tenant to upgrade for history', () => {
		renderBar(7, 'Enterprise');

		const title = screen.getByRole('button', { name: /30d/ }).getAttribute('title');

		expect(title).toBe('30d of history is not included in your agreement');
		expect(title).not.toContain('Upgrade');
	});

	it('describes an enabled range the same way for every tier', () => {
		renderBar(30, 'Enterprise');

		expect(screen.getByRole('button', { name: /30d/ }).getAttribute('title')).toBe(
			'Show 30d of history'
		);
	});
});
