// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// Resource usage thresholds for color-coded status display.
// Values are percentages (0-100).

export const CPU_WARNING_THRESHOLD = 80;
export const CPU_CRITICAL_THRESHOLD = 95;

export const MEMORY_WARNING_THRESHOLD = 80;
export const MEMORY_CRITICAL_THRESHOLD = 95;

export const DISK_WARNING_THRESHOLD = 80;
export const DISK_CRITICAL_THRESHOLD = 95;

// Temperature thresholds in degrees Celsius.
export const TEMP_WARNING_CELSIUS = 55;
export const TEMP_CRITICAL_CELSIUS = 80;

// Headline per-machine monthly price for the Pro tier, in whole US dollars, used by
// upsell copy in the signed-in app. This must match the live Stripe price the checkout
// actually charges (lookup key vord_pro_monthly_licensed) and the figure every marketing
// surface quotes. A stale value here misquotes the price to a customer who is one click
// from paying it.
export const PRO_PRICE_PER_MACHINE_USD = 5;
