// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { SubscriptionDto } from '$lib/api/types';

/**
 * Whether the tenant may touch its alert rules at all: enable or disable one, and choose the
 * machines it watches.
 *
 * This mirrors the server's Pro gate, which refuses a lapsed paid tier exactly as flatly as it
 * refuses Free, so the tier alone is not the question — an Active status is a conjunct. Two
 * deliberate optimisms remain: a self-hosted deployment has no entitlements to check, and a missing
 * subscription is read as entitled, because the billing API being briefly unavailable must not
 * present to the tenant as a downgrade. The server refuses anything the tenant is not entitled to
 * regardless of what was rendered, so optimism here costs a 403, never an escalation.
 */
export function canManageAlertRules(
	subscription: SubscriptionDto | null | undefined,
	selfHosted: boolean
): boolean {
	if (selfHosted) {
		return true;
	}

	if (subscription === null || subscription === undefined) {
		return true;
	}

	return (subscription.tier === 'Pro' || subscription.tier === 'Team') &&
		subscription.status === 'Active';
}

/**
 * Whether the tenant may author rules: create a custom one, retune a built-in, or delete a custom
 * one.
 *
 * The Team half of this is tier-only, matching SubscriptionPolicy.RequiresTeam, which has no status
 * test. That is not an oversight in either place: every endpoint the Team check gates also carries
 * the Pro gate, so the status requirement arrives through canManageAlertRules rather than being
 * restated here. Composing the two keeps the asymmetry where the server puts it — a lapsed Team
 * tenant is refused because it is lapsed, not because it stopped being Team.
 */
export function canAuthorAlertRules(
	subscription: SubscriptionDto | null | undefined,
	selfHosted: boolean
): boolean {
	if (canManageAlertRules(subscription, selfHosted) === false) {
		return false;
	}

	if (selfHosted) {
		return true;
	}

	if (subscription === null || subscription === undefined) {
		return true;
	}

	return subscription.tier === 'Team';
}
