// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, afterEach } from 'vitest';
import {
    formatDate,
    formatDateTime,
    formatRelativeTime,
    formatNumber,
    formatPercentage,
    formatBytes,
    formatUptime
} from './format';

describe('formatDate', () => {
    it('should return em dash for null input', () => {
        expect(formatDate(null)).toBe('\u2014');
    });

    it('should return em dash for empty string', () => {
        expect(formatDate('')).toBe('\u2014');
    });

    it('should return em dash for invalid date string', () => {
        expect(formatDate('not-a-date')).toBe('\u2014');
    });

    it('should format a valid date string', () => {
        const result = formatDate('2026-01-15T12:00:00Z');
        expect(result).toContain('Jan');
        expect(result).toContain('15');
        expect(result).toContain('2026');
    });
});

describe('formatDateTime', () => {
    it('should return em dash for null input', () => {
        expect(formatDateTime(null)).toBe('\u2014');
    });

    it('should return em dash for empty string', () => {
        expect(formatDateTime('')).toBe('\u2014');
    });

    it('should return em dash for invalid date string', () => {
        expect(formatDateTime('garbage')).toBe('\u2014');
    });

    it('should format a valid date-time string with time', () => {
        const result = formatDateTime('2026-06-15T14:30:00Z');
        expect(result).toContain('Jun');
        expect(result).toContain('15');
        expect(result).toContain('2026');
    });
});

describe('formatRelativeTime', () => {
    afterEach(() => {
        vi.useRealTimers();
    });

    it('should return Never for null input', () => {
        expect(formatRelativeTime(null)).toBe('Never');
    });

    it('should return Just now for times less than 60 seconds ago', () => {
        vi.useFakeTimers();
        const now = new Date('2026-02-22T12:00:30Z');
        vi.setSystemTime(now);

        expect(formatRelativeTime('2026-02-22T12:00:00Z')).toBe('Just now');
    });

    it('should return minutes ago for times less than 60 minutes ago', () => {
        vi.useFakeTimers();
        const now = new Date('2026-02-22T12:05:00Z');
        vi.setSystemTime(now);

        expect(formatRelativeTime('2026-02-22T12:00:00Z')).toBe('5m ago');
    });

    it('should return hours ago for times less than 24 hours ago', () => {
        vi.useFakeTimers();
        const now = new Date('2026-02-22T15:00:00Z');
        vi.setSystemTime(now);

        expect(formatRelativeTime('2026-02-22T12:00:00Z')).toBe('3h ago');
    });

    it('should return days ago for times less than 7 days ago', () => {
        vi.useFakeTimers();
        const now = new Date('2026-02-25T12:00:00Z');
        vi.setSystemTime(now);

        expect(formatRelativeTime('2026-02-22T12:00:00Z')).toBe('3d ago');
    });

    it('should return formatted date for times more than 7 days ago', () => {
        vi.useFakeTimers();
        const now = new Date('2026-03-10T12:00:00Z');
        vi.setSystemTime(now);

        const result = formatRelativeTime('2026-02-22T12:00:00Z');
        expect(result).toContain('Feb');
        expect(result).toContain('22');
        expect(result).toContain('2026');
    });

    it('should return Just now for future dates since negative diff is less than 60', () => {
        vi.useFakeTimers();
        const now = new Date('2026-02-22T12:00:00Z');
        vi.setSystemTime(now);

        expect(formatRelativeTime('2026-02-25T12:00:00Z')).toBe('Just now');
    });
});

describe('formatNumber', () => {
    it('should format a small number', () => {
        expect(formatNumber(42)).toBe('42');
    });

    it('should format a large number with commas', () => {
        expect(formatNumber(1234567)).toBe('1,234,567');
    });

    it('should format zero', () => {
        expect(formatNumber(0)).toBe('0');
    });

    it('should format a negative number', () => {
        expect(formatNumber(-42)).toBe('-42');
    });
});

describe('formatPercentage', () => {
    it('should return 0% when total is 0', () => {
        expect(formatPercentage(5, 0)).toBe('0%');
    });

    it('should calculate percentage correctly', () => {
        expect(formatPercentage(50, 100)).toBe('50%');
    });

    it('should round to nearest integer', () => {
        expect(formatPercentage(1, 3)).toBe('33%');
    });

    it('should handle 100%', () => {
        expect(formatPercentage(100, 100)).toBe('100%');
    });

    it('should allow values exceeding total', () => {
        expect(formatPercentage(200, 100)).toBe('200%');
    });

    it('should handle negative value', () => {
        expect(formatPercentage(-50, 100)).toBe('-50%');
    });
});

describe('formatBytes', () => {
    it('should return 0 B for zero bytes', () => {
        expect(formatBytes(0)).toBe('0 B');
    });

    it('should format bytes', () => {
        expect(formatBytes(500)).toBe('500 B');
    });

    it('should format kilobytes', () => {
        expect(formatBytes(1024)).toBe('1.0 KB');
    });

    it('should format megabytes', () => {
        expect(formatBytes(1048576)).toBe('1.0 MB');
    });

    it('should format gigabytes', () => {
        expect(formatBytes(1073741824)).toBe('1.0 GB');
    });

    it('should format terabytes', () => {
        expect(formatBytes(1099511627776)).toBe('1.0 TB');
    });

    it('should round large values in a unit', () => {
        // 15 GB = 15 * 1024^3 = 16106127360
        expect(formatBytes(16106127360)).toBe('15 GB');
    });

    it('should return em dash for NaN', () => {
        expect(formatBytes(NaN)).toBe('\u2014');
    });

    it('should return em dash for Infinity', () => {
        expect(formatBytes(Infinity)).toBe('\u2014');
    });

    it('should format negative bytes with minus prefix', () => {
        expect(formatBytes(-500)).toBe('-500 B');
    });

    it('should format negative kilobytes with minus prefix', () => {
        expect(formatBytes(-1024)).toBe('-1.0 KB');
    });
});

describe('formatUptime', () => {
    it('should return em dash for zero seconds', () => {
        expect(formatUptime(0)).toBe('\u2014');
    });

    it('should return em dash for negative seconds', () => {
        expect(formatUptime(-100)).toBe('\u2014');
    });

    it('should format minutes only', () => {
        expect(formatUptime(300)).toBe('5m');
    });

    it('should format hours and minutes', () => {
        expect(formatUptime(3660)).toBe('1h 1m');
    });

    it('should format days and hours', () => {
        expect(formatUptime(90000)).toBe('1d 1h');
    });

    it('should format multiple days', () => {
        expect(formatUptime(259200)).toBe('3d 0h');
    });

    it('should return 0m for sub-minute seconds', () => {
        expect(formatUptime(59)).toBe('0m');
    });

    it('should format exactly one hour', () => {
        expect(formatUptime(3600)).toBe('1h 0m');
    });
});
