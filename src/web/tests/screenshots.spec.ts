// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { test } from '@playwright/test';
import path from 'node:path';
import fs from 'node:fs/promises';

// Captures marketing screenshots from the fleet UI running in mock mode and
// writes them straight into the vord-internal marketing repo's static assets.

const OUTPUT_DIR = path.resolve(
	import.meta.dirname,
	'../../../../vord-internal/src/marketing/static/screenshots'
);

test.beforeAll(async () => {
	await fs.mkdir(OUTPUT_DIR, { recursive: true });
});

test('fleet dashboard', async ({ page }) => {
	await page.goto('/dashboard');
	await page.waitForLoadState('networkidle');
	await page.screenshot({
		path: path.join(OUTPUT_DIR, 'fleet-dashboard.png'),
		fullPage: false
	});
});

test('machine detail — overview tab', async ({ page }) => {
	await page.goto('/machines/1');
	await page.waitForLoadState('networkidle');
	await page.screenshot({
		path: path.join(OUTPUT_DIR, 'machine-detail-overview.png'),
		fullPage: false
	});
});

// The Hardware tab is content-dense — System Info, CPU/memory gauges, Disk Usage,
// fans, PSUs, temps, SMART. Use a taller viewport so a single screenshot
// captures the operator-relevant detail rather than the marketing reader having
// to scroll mentally through context.
test.describe('hardware tabs', () => {
	test.use({ viewport: { width: 1440, height: 2000 } });

	test('machine detail — hardware tab', async ({ page }) => {
		await page.goto('/machines/1');
		await page.waitForLoadState('networkidle');
		await page.getByRole('tab', { name: 'Hardware' }).click();
		await page.waitForTimeout(400);
		await page.screenshot({
			path: path.join(OUTPUT_DIR, 'machine-detail-hardware.png'),
			fullPage: false
		});
	});

	test('machine warning — db-primary hardware tab', async ({ page }) => {
		await page.goto('/machines/3');
		await page.waitForLoadState('networkidle');
		await page.getByRole('tab', { name: 'Hardware' }).click();
		await page.waitForTimeout(400);
		await page.screenshot({
			path: path.join(OUTPUT_DIR, 'machine-warning-hardware.png'),
			fullPage: false
		});
	});
});

test('ssh sessions', async ({ page }) => {
	await page.goto('/machines/ssh-sessions');
	await page.waitForLoadState('networkidle');
	await page.screenshot({
		path: path.join(OUTPUT_DIR, 'ssh-sessions.png'),
		fullPage: false
	});
});
