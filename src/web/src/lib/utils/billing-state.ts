// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { SubscriptionDto, CatalogItemDto } from '$lib/api/types';

export type BillingPageState = 'free' | 'active' | 'pending-change' | 'past-due' | 'canceled';

/**
 * Derives the top-level billing page state from the subscription DTO.
 * Precedence: canceled > pending-change > past-due > free > active.
 */
export function deriveBillingPageState(sub: SubscriptionDto | null): BillingPageState {
	if (sub === null) return 'free';
	if (sub.status === 'Canceled') return 'canceled';
	if (sub.cancelAtPeriodEnd) return 'pending-change';
	if (sub.status === 'PastDue') return 'past-due';
	if (sub.tier === 'Free') return 'free';

	return 'active';
}

/** Maps the wire interval ("monthly"/"annual") to its display label, or null when absent. */
export function billingIntervalLabel(interval: string | null): string | null {
	if (interval === 'monthly') return 'Monthly';
	if (interval === 'annual') return 'Annual';

	return null;
}

/** Finds the catalog entry for a tier + interval, or null when the catalog has no match. */
export function findCatalogPrice(
	catalog: CatalogItemDto[],
	tier: string,
	interval: string
): CatalogItemDto | null {
	return catalog.find((i) => i.tier === tier && i.interval === interval) ?? null;
}

/**
 * Finds the catalog entry for a tier, preferring the given interval but falling back to the
 * tier's other interval so a tier with any price is never hidden. Null only when the tier
 * has no catalog price at all.
 */
export function findCatalogPriceWithFallback(
	catalog: CatalogItemDto[],
	tier: string,
	preferredInterval: string
): CatalogItemDto | null {
	const exact = findCatalogPrice(catalog, tier, preferredInterval);
	if (exact !== null) return exact;

	const otherInterval = preferredInterval === 'monthly' ? 'annual' : 'monthly';

	return findCatalogPrice(catalog, tier, otherInterval);
}

/** Per-machine monthly-equivalent price in cents (annual prices divided by twelve). */
export function monthlyEquivalentCents(item: CatalogItemDto): number {
	if (item.interval === 'annual') {
		return Math.round(item.unitAmountCents / 12);
	}

	return item.unitAmountCents;
}
