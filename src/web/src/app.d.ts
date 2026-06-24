// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { UserDto, SubscriptionDto } from '$lib/api/types';

declare global {
	namespace App {
		interface Locals {
			user: UserDto | null;
			// Raw vord_csrf antiforgery cookie value paired with user.csrfToken. SSR mutations
			// forward this cookie and echo user.csrfToken in the X-CSRF-TOKEN header so the
			// backend's double-submit gate accepts them.
			csrfCookie: string | undefined;
		}
		interface PageData {
			user: UserDto | null;
			subscription?: SubscriptionDto | null;
		}
	}
}

export {};
