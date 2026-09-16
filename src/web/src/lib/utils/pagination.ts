// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { MAX_PAGE_SIZE } from './constants';

export interface PaginationParams {
	page: number;
	pageSize: number;
}

export function parsePaginationParams(
	url: URL,
	defaults: { page?: number; pageSize?: number; maxPageSize?: number } = {}
): PaginationParams {
	const defaultPage = defaults.page ?? 1;
	const defaultPageSize = defaults.pageSize ?? 25;
	// Clamped rather than refused, unlike the API. This reads a URL a person may have typed, and the
	// page it feeds paginates on the metadata the API returns, so a clamped size still reaches every
	// row — it only makes the pages shorter. Refusing would put an error page in front of a typo.
	const maxPageSize = defaults.maxPageSize ?? MAX_PAGE_SIZE;

	const rawPage = parseInt(url.searchParams.get('page') ?? String(defaultPage), 10);
	const rawPageSize = parseInt(url.searchParams.get('pageSize') ?? String(defaultPageSize), 10);

	const page = Number.isNaN(rawPage) ? defaultPage : Math.max(1, rawPage);
	const pageSize = Number.isNaN(rawPageSize)
		? defaultPageSize
		: Math.min(maxPageSize, Math.max(1, rawPageSize));

	return { page, pageSize };
}
