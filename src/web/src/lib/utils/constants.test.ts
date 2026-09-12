// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { PRO_PRICE_PER_MACHINE_USD } from './constants';

describe('PRO_PRICE_PER_MACHINE_USD', () => {
    it('should be the $5 charged by the live Stripe price vord_pro_monthly_licensed', () => {
        expect(PRO_PRICE_PER_MACHINE_USD).toBe(5);
    });
});

describe('members upsell copy', () => {
    // The signed-in Free-tier upsell used to hardcode $3 — the real price of a legacy
    // Stripe price that has since been archived — while every marketing surface said $5.
    // A user on the members page saw one number and the checkout charged another. This
    // asserts the page renders the shared constant rather than a literal of its own.
    const source = readFileSync(
        resolve(process.cwd(), 'src/routes/(app)/settings/members/+page.svelte'),
        'utf-8'
    );

    it('should quote the Pro price from the shared constant', () => {
        expect(source).toContain('PRO_PRICE_PER_MACHINE_USD');
        expect(source).toContain('${PRO_PRICE_PER_MACHINE_USD}/host/month');
    });

    it('should not hardcode any dollar-amount price in the upsell', () => {
        const upsell = source.slice(source.indexOf('Upgrade to Pro to invite team members'));
        const literalPrice = /\$\d+\/host\/month/;
        expect(literalPrice.test(upsell.slice(0, 200))).toBe(false);
    });
});
