// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// The tier names the server sends (SubscriptionTier.ToString()). Enterprise carries every Team
// feature with limits set by an agreement, and is invoiced outside Stripe — so "has Team features"
// and "is billed through Stripe" are different questions and must never share a helper.
export const TIER_NAMES = ['Free', 'Pro', 'Team', 'Enterprise'] as const;

export type TierName = (typeof TIER_NAMES)[number];

const PRO_FEATURE_TIERS: ReadonlySet<string> = new Set(['Pro', 'Team', 'Enterprise']);
const TEAM_FEATURE_TIERS: ReadonlySet<string> = new Set(['Team', 'Enterprise']);
const STRIPE_BILLED_TIERS: ReadonlySet<string> = new Set(['Pro', 'Team']);

export function hasProFeatures(tier: string | null | undefined): boolean {
	return tier !== null && tier !== undefined && PRO_FEATURE_TIERS.has(tier);
}

export function hasTeamFeatures(tier: string | null | undefined): boolean {
	return tier !== null && tier !== undefined && TEAM_FEATURE_TIERS.has(tier);
}

export function isEnterprise(tier: string | null | undefined): boolean {
	return tier === 'Enterprise';
}

export function isStripeBilled(tier: string | null | undefined): boolean {
	return tier !== null && tier !== undefined && STRIPE_BILLED_TIERS.has(tier);
}

export function tierBadgeClasses(tier: string): string {
	if (tier === 'Pro') {
		return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400';
	}
	if (tier === 'Team') {
		return 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-400';
	}
	if (tier === 'Enterprise') {
		return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400';
	}

	return 'bg-surface-100 text-surface-700 dark:bg-surface-700 dark:text-surface-300';
}

// An agreement states "no limit" as the largest 32-bit integer, and it does so for every limit it
// can carry (machines, members, alert rules, integrations). Rendering that number, or computing a
// percentage of it, reads as a bug, so every place that shows a limit asks isLimited first.
export const UNLIMITED_LIMIT = 2147483647;

export function isLimited(limit: number | null | undefined): limit is number {
	return limit !== null && limit !== undefined && limit !== UNLIMITED_LIMIT;
}

export function usageLabel(count: number, limit: number | null | undefined): string {
	if (isLimited(limit) === false) {
		return 'Unlimited';
	}

	return `${count} / ${limit}`;
}
