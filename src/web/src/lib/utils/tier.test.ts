// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import {
	hasProFeatures,
	hasTeamFeatures,
	isEnterprise,
	isLimited,
	isStripeBilled,
	tierBadgeClasses,
	TIER_NAMES,
	UNLIMITED_LIMIT,
	usageLabel
} from './tier';

describe('tier helpers', () => {
	// One row per tier the server can send, so a tier added later fails here until it is classified.
	const table: Array<[string, { pro: boolean; team: boolean; enterprise: boolean; stripe: boolean }]> = [
		['Free', { pro: false, team: false, enterprise: false, stripe: false }],
		['Pro', { pro: true, team: false, enterprise: false, stripe: true }],
		['Team', { pro: true, team: true, enterprise: false, stripe: true }],
		['Enterprise', { pro: true, team: true, enterprise: true, stripe: false }]
	];

	it('classifies every tier the server sends', () => {
		expect(table.map(([tier]) => tier)).toEqual([...TIER_NAMES]);
	});

	it.each(table)('%s', (tier, expected) => {
		expect(hasProFeatures(tier)).toBe(expected.pro);
		expect(hasTeamFeatures(tier)).toBe(expected.team);
		expect(isEnterprise(tier)).toBe(expected.enterprise);
		expect(isStripeBilled(tier)).toBe(expected.stripe);
	});

	it('treats a missing or unknown tier as having no paid features', () => {
		for (const tier of [null, undefined, '', 'None', 'enterprise']) {
			expect(hasProFeatures(tier)).toBe(false);
			expect(hasTeamFeatures(tier)).toBe(false);
			expect(isStripeBilled(tier)).toBe(false);
		}
	});

	it('gives Enterprise its own badge', () => {
		expect(tierBadgeClasses('Enterprise')).not.toEqual(tierBadgeClasses('Team'));
		expect(tierBadgeClasses('Enterprise')).not.toEqual(tierBadgeClasses('Free'));
	});
});

describe('unlimited limits', () => {
	it('uses the largest 32-bit integer the server sends for an unlimited allowance', () => {
		expect(UNLIMITED_LIMIT).toBe(2147483647);
	});

	it('treats only a finite limit as a limit', () => {
		expect(isLimited(0)).toBe(true);
		expect(isLimited(5)).toBe(true);
		expect(isLimited(UNLIMITED_LIMIT - 1)).toBe(true);
		expect(isLimited(UNLIMITED_LIMIT)).toBe(false);
		expect(isLimited(null)).toBe(false);
		expect(isLimited(undefined)).toBe(false);
	});

	it('shows usage against a finite limit', () => {
		expect(usageLabel(12, 40)).toBe('12 / 40');
		expect(usageLabel(0, 0)).toBe('0 / 0');
	});

	it('shows Unlimited, never the sentinel, when there is no limit', () => {
		expect(usageLabel(12, UNLIMITED_LIMIT)).toBe('Unlimited');
		expect(usageLabel(12, null)).toBe('Unlimited');
		expect(usageLabel(12, undefined)).toBe('Unlimited');
	});
});
