// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { getStatusFilterOptions, normaliseStatusFilter } from './machine-status';

describe('getStatusFilterOptions', () => {
	it('offers every status badge, then Online for any health but Offline, then Paused', () => {
		expect(getStatusFilterOptions()).toEqual([
			{ value: '', label: 'All Status' },
			{ value: 'Healthy', label: 'Healthy' },
			{ value: 'Warning', label: 'Warning' },
			{ value: 'Critical', label: 'Critical' },
			{ value: 'Offline', label: 'Offline' },
			{ value: 'Online', label: 'Online (any health)' },
			{ value: 'Paused', label: 'Paused' }
		]);
	});
});

describe('normaliseStatusFilter', () => {
	it.each(getStatusFilterOptions().filter((option) => option.value !== ''))(
		'maps any casing of $value to the option value',
		({ value }) => {
			expect(normaliseStatusFilter(value.toLowerCase())).toBe(value);
			expect(normaliseStatusFilter(value.toUpperCase())).toBe(value);
			expect(normaliseStatusFilter(value)).toBe(value);
		}
	);

	it('ignores whitespace around a status', () => {
		expect(normaliseStatusFilter('  critical ')).toBe('Critical');
	});

	it.each([null, undefined, '', '   ', 'all', 'degraded', 'healthy,warning'])(
		'maps %j to no filter',
		(raw) => {
			expect(normaliseStatusFilter(raw)).toBe('');
		}
	);
});
