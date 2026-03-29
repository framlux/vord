// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

/**
 * Builds a URL query string from a record of parameters, filtering out
 * null, undefined, and empty string values. Returns the query string
 * without a leading '?' (empty string if no valid params).
 */
export function buildQueryString(params: Record<string, unknown>): string {
	const query = new URLSearchParams();
	for (const [key, value] of Object.entries(params)) {
		if (value === null || value === undefined || value === '') {
			continue;
		}
		query.set(key, String(value));
	}

	return query.toString();
}
