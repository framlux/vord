// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { canAuthorAlertRules, canManageAlertRules } from './alert-entitlement';
import type { SubscriptionDto } from '$lib/api/types';

function sub(tier: string, status: string): SubscriptionDto {
	return {
		tier,
		status,
		machineLimit: 1000,
		machineCount: 0,
		retentionDays: 30,
		currentPeriodEnd: null,
		cancelAtPeriodEnd: false,
		billingInterval: 'monthly',
		pendingAction: null,
		alertRuleLimit: 10,
		alertRuleCount: 0,
		webhookLimit: 3,
		webhookCount: 0
	};
}

describe('canManageAlertRules', () => {
	it('admits an active Pro or Team tenant', () => {
		expect(canManageAlertRules(sub('Pro', 'Active'), false)).toBe(true);
		expect(canManageAlertRules(sub('Team', 'Active'), false)).toBe(true);
	});

	it('refuses Free whatever its status', () => {
		expect(canManageAlertRules(sub('Free', 'Active'), false)).toBe(false);
		expect(canManageAlertRules(sub('Free', 'PastDue'), false)).toBe(false);
	});

	it('refuses a paid tier that is not Active, matching the server Pro gate', () => {
		expect(canManageAlertRules(sub('Pro', 'PastDue'), false)).toBe(false);
		expect(canManageAlertRules(sub('Pro', 'Canceled'), false)).toBe(false);
		expect(canManageAlertRules(sub('Team', 'PastDue'), false)).toBe(false);
		expect(canManageAlertRules(sub('Team', 'Canceled'), false)).toBe(false);
	});

	it('reads a missing subscription optimistically rather than as a downgrade', () => {
		expect(canManageAlertRules(null, false)).toBe(true);
		expect(canManageAlertRules(undefined, false)).toBe(true);
	});

	it('admits a self-hosted deployment whatever the subscription row says', () => {
		expect(canManageAlertRules(sub('Free', 'Canceled'), true)).toBe(true);
	});
});

describe('canAuthorAlertRules', () => {
	it('admits only an active Team tenant', () => {
		expect(canAuthorAlertRules(sub('Team', 'Active'), false)).toBe(true);
		expect(canAuthorAlertRules(sub('Pro', 'Active'), false)).toBe(false);
		expect(canAuthorAlertRules(sub('Free', 'Active'), false)).toBe(false);
	});

	it('refuses a lapsed Team tenant, because the Pro gate refuses it too', () => {
		expect(canAuthorAlertRules(sub('Team', 'PastDue'), false)).toBe(false);
		expect(canAuthorAlertRules(sub('Team', 'Canceled'), false)).toBe(false);
	});

	it('never admits more than canManageAlertRules does', () => {
		const tiers = ['Free', 'Pro', 'Team'];
		const statuses = ['Active', 'PastDue', 'Canceled', 'Trialing'];

		for (const tier of tiers) {
			for (const status of statuses) {
				const subscription = sub(tier, status);
				if (canAuthorAlertRules(subscription, false)) {
					expect(canManageAlertRules(subscription, false)).toBe(true);
				}
			}
		}
	});

	it('reads a missing subscription and a self-hosted deployment optimistically', () => {
		expect(canAuthorAlertRules(null, false)).toBe(true);
		expect(canAuthorAlertRules(undefined, false)).toBe(true);
		expect(canAuthorAlertRules(sub('Free', 'Canceled'), true)).toBe(true);
	});
});

// The affordance table, interface side. The interface must never offer an action the API will
// refuse, and that rule is written twice in two languages in two layers: these predicates decide
// which controls are drawn, and the endpoint guards decide which requests are honoured. Nothing
// connects them, so either side can move alone and the failure is silent in both directions —
// remove a gate and Free gets a screen the server refuses; tighten one and Pro is shown a control
// that answers 403.
//
// The server half of this same table is pinned in AlertRuleTierGuardDriftTests, written out in the
// same terms. Neither test can prove the other still agrees; each fails the moment its own side
// moves, which is what makes a drift reviewable instead of invisible.
//
//                            | Free | Pro  | Team
//   See rules and coverage   | yes (read-only, every tier — ungated beyond tenant scope)
//   Enable / disable         | no   | yes  | yes    (built-ins)
//   Assign machines          | no   | yes  | yes    (built-ins)
//   Edit thresholds          | no   | no   | yes
//   Create / delete custom   | no   | no   | yes
describe('the alert affordance table', () => {
	const rows: Array<{
		tier: string;
		mayManage: boolean;
		mayAuthor: boolean;
	}> = [
		{ tier: 'Free', mayManage: false, mayAuthor: false },
		{ tier: 'Pro', mayManage: true, mayAuthor: false },
		{ tier: 'Team', mayManage: true, mayAuthor: true }
	];

	for (const row of rows) {
		it(`gives an active ${row.tier} tenant exactly the controls its row allows`, () => {
			const subscription = sub(row.tier, 'Active');

			// Enable/disable and assign machines — the two things the row calls "manage".
			expect(canManageAlertRules(subscription, false)).toBe(row.mayManage);

			// Edit thresholds and create/delete custom rules — what the row calls "author".
			expect(canAuthorAlertRules(subscription, false)).toBe(row.mayAuthor);
		});
	}

	it('never offers authoring without also offering management, which the server requires first', () => {
		// Every Team-gated alert endpoint also carries the Pro gate, so a tenant that may author but
		// not manage is a state the interface could render and the API would always refuse.
		for (const row of rows) {
			for (const status of ['Active', 'PastDue', 'Canceled', 'Trialing']) {
				const subscription = sub(row.tier, status);
				if (canAuthorAlertRules(subscription, false)) {
					expect(canManageAlertRules(subscription, false)).toBe(true);
				}
			}
		}
	});

	it('withholds every control from a lapsed paid tier, matching the Pro gate that consults status', () => {
		for (const tier of ['Pro', 'Team']) {
			for (const status of ['PastDue', 'Canceled']) {
				expect(canManageAlertRules(sub(tier, status), false)).toBe(false);
				expect(canAuthorAlertRules(sub(tier, status), false)).toBe(false);
			}
		}
	});
});
