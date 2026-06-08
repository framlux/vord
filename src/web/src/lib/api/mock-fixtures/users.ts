// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { UserDto, SubscriptionDto } from '../types';

export const mockUser: UserDto = {
	id: 1,
	name: 'Alex Reiter',
	email: 'alex@framlux.io',
	avatar: '',
	isGlobalAdmin: false,
	uniqueId: 'mock-user-001',
	needsOnboarding: false,
	activeTenantId: 1,
	tenants: [
		{ tenantId: 1, tenantName: 'Framlux Production', role: '1' },
		{ tenantId: 2, tenantName: 'Framlux Lab', role: '1' }
	]
};

export const mockSubscription: SubscriptionDto = {
	tier: 'Pro',
	status: 'Active',
	machineLimit: 1000,
	machineCount: 18,
	retentionDays: 60,
	currentPeriodEnd: new Date(Date.now() + 1000 * 60 * 60 * 24 * 21).toISOString(),
	cancelAtPeriodEnd: false,
	pendingAction: null,
	alertRuleLimit: 50,
	alertRuleCount: 8,
	webhookLimit: 10,
	webhookCount: 2
};
