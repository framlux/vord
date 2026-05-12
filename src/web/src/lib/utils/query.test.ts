// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { buildQueryString } from './query';

describe('buildQueryString', () => {
    it('should return empty string for empty params object', () => {
        expect(buildQueryString({})).toBe('');
    });

    it('should build query string from a single parameter', () => {
        expect(buildQueryString({ name: 'alice' })).toBe('name=alice');
    });

    it('should build query string from multiple parameters', () => {
        const result = buildQueryString({ page: 1, size: 25 });
        expect(result).toContain('page=1');
        expect(result).toContain('size=25');
        expect(result).toContain('&');
    });

    it('should filter out null values', () => {
        expect(buildQueryString({ a: 'keep', b: null })).toBe('a=keep');
    });

    it('should filter out undefined values', () => {
        expect(buildQueryString({ a: 'keep', b: undefined })).toBe('a=keep');
    });

    it('should filter out empty string values', () => {
        expect(buildQueryString({ a: 'keep', b: '' })).toBe('a=keep');
    });

    it('should return empty string when all values are filtered out', () => {
        expect(buildQueryString({ a: null, b: undefined, c: '' })).toBe('');
    });

    it('should convert number values to strings', () => {
        expect(buildQueryString({ count: 42 })).toBe('count=42');
    });

    it('should include zero as a valid value', () => {
        expect(buildQueryString({ offset: 0 })).toBe('offset=0');
    });

    it('should convert boolean true to string', () => {
        expect(buildQueryString({ active: true })).toBe('active=true');
    });

    it('should convert boolean false to string', () => {
        expect(buildQueryString({ active: false })).toBe('active=false');
    });

    it('should encode special characters in values', () => {
        const result = buildQueryString({ q: 'hello world' });
        expect(result).toBe('q=hello+world');
    });

    it('should encode special characters in keys', () => {
        const result = buildQueryString({ 'my key': 'value' });
        expect(result).toContain('my+key=value');
    });

    it('should handle ampersand in values', () => {
        const result = buildQueryString({ q: 'a&b' });
        expect(result).toBe('q=a%26b');
    });

    it('should handle mixed valid and filtered values', () => {
        const result = buildQueryString({
            keep1: 'yes',
            drop1: null,
            keep2: 100,
            drop2: undefined,
            keep3: false,
            drop3: ''
        });
        expect(result).toContain('keep1=yes');
        expect(result).toContain('keep2=100');
        expect(result).toContain('keep3=false');
        expect(result).not.toContain('drop1');
        expect(result).not.toContain('drop2');
        expect(result).not.toContain('drop3');
    });

    it('should handle negative number values', () => {
        expect(buildQueryString({ offset: -5 })).toBe('offset=-5');
    });

    it('should handle floating point number values', () => {
        expect(buildQueryString({ ratio: 3.14 })).toBe('ratio=3.14');
    });
});
