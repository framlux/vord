// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { RequestHandler } from './$types';

export const GET: RequestHandler = async () => {
	return new Response('ok', { status: 200 });
};
