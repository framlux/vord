// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// Captured once at module import so fixture timestamps stay stable within a session
// but always look fresh relative to whenever the dev server was started.
const now = Date.now();

export function secondsAgo(seconds: number): string {
	return new Date(now - seconds * 1000).toISOString();
}

export function minutesAgo(minutes: number): string {
	return secondsAgo(minutes * 60);
}

export function hoursAgo(hours: number): string {
	return minutesAgo(hours * 60);
}

export function daysAgo(days: number): string {
	return hoursAgo(days * 24);
}

export function daysFromNow(days: number): string {
	return new Date(now + days * 24 * 60 * 60 * 1000).toISOString();
}
