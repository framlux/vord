// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { defineConfig } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Marketing-screenshot capture configuration. Drives the fleet UI in mock mode
// (VORD_API_MOCK=true) and writes PNGs to the vord-internal marketing repo.

const certPath = path.resolve(import.meta.dirname, '../certs/localhost+1.pem');
const certsExist = fs.existsSync(certPath);
const baseURL = certsExist ? 'https://localhost:5173' : 'http://localhost:5173';

export default defineConfig({
	testDir: './tests',
	testMatch: '**/screenshots.spec.ts',
	fullyParallel: false,
	workers: 1,
	reporter: 'list',
	use: {
		baseURL,
		viewport: { width: 1440, height: 900 },
		ignoreHTTPSErrors: true,
		deviceScaleFactor: 2,
		colorScheme: 'dark'
	},
	webServer: {
		command: 'pnpm dev',
		env: { VORD_API_MOCK: 'true' },
		url: baseURL,
		reuseExistingServer: true,
		ignoreHTTPSErrors: true,
		timeout: 90_000
	}
});
