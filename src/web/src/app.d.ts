// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { UserDto } from '$lib/api/types';

declare global {
	namespace App {
		interface Locals {
			user: UserDto | null;
		}
		interface PageData {
			user: UserDto | null;
		}
	}
}

export {};
