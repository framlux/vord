// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

export interface StatusFilterOption {
	value: string;
	label: string;
}

// The machine list's status filter. Each name matches the status badge a row shows: Healthy, Warning,
// Critical and Offline are the swept health, Paused replaces health for hosts past the machine limit,
// and Online is every health but Offline. The values are capitalised to match the links that point at
// the list, such as the over-limit notice's ?status=Paused.
export function getStatusFilterOptions(): StatusFilterOption[] {
	return [
		{ value: '', label: 'All Status' },
		{ value: 'Healthy', label: 'Healthy' },
		{ value: 'Warning', label: 'Warning' },
		{ value: 'Critical', label: 'Critical' },
		{ value: 'Offline', label: 'Offline' },
		{ value: 'Online', label: 'Online (any health)' },
		{ value: 'Paused', label: 'Paused' }
	];
}

// A status in the URL may have been typed or bookmarked in any case. Returns the option value it names,
// or an empty string when it names none, which is also what the server does with an unknown status:
// no filter.
export function normaliseStatusFilter(raw: string | null | undefined): string {
	if (!raw) {
		return '';
	}

	const wanted = raw.trim().toLowerCase();
	const match = getStatusFilterOptions().find((option) => option.value !== '' && option.value.toLowerCase() === wanted);

	return match?.value ?? '';
}
