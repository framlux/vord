// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { parsePaginationParams } from './pagination';

function makeUrl(params: Record<string, string> = {}): URL {
    const url = new URL('https://example.com/api/items');
    for (const [key, value] of Object.entries(params)) {
        url.searchParams.set(key, value);
    }

    return url;
}

describe('parsePaginationParams', () => {
    describe('default values', () => {
        it('should return page=1 and pageSize=25 when no params are present', () => {
            const result = parsePaginationParams(makeUrl());
            expect(result).toEqual({ page: 1, pageSize: 25 });
        });

        it('should use custom default page when provided', () => {
            const result = parsePaginationParams(makeUrl(), { page: 5 });
            expect(result.page).toBe(5);
        });

        it('should use custom default pageSize when provided', () => {
            const result = parsePaginationParams(makeUrl(), { pageSize: 50 });
            expect(result.pageSize).toBe(50);
        });

        it('should use custom maxPageSize when provided', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '200' }), { maxPageSize: 200 });
            expect(result.pageSize).toBe(200);
        });
    });

    describe('parsing page parameter', () => {
        it('should parse a valid page number from URL', () => {
            const result = parsePaginationParams(makeUrl({ page: '3' }));
            expect(result.page).toBe(3);
        });

        it('should clamp page to minimum of 1 when zero is provided', () => {
            const result = parsePaginationParams(makeUrl({ page: '0' }));
            expect(result.page).toBe(1);
        });

        it('should clamp page to minimum of 1 when negative value is provided', () => {
            const result = parsePaginationParams(makeUrl({ page: '-5' }));
            expect(result.page).toBe(1);
        });

        it('should fall back to default page when NaN is provided', () => {
            const result = parsePaginationParams(makeUrl({ page: 'abc' }));
            expect(result.page).toBe(1);
        });

        it('should fall back to custom default page when NaN is provided', () => {
            const result = parsePaginationParams(makeUrl({ page: 'abc' }), { page: 10 });
            expect(result.page).toBe(10);
        });

        it('should accept page=1 as a valid boundary value', () => {
            const result = parsePaginationParams(makeUrl({ page: '1' }));
            expect(result.page).toBe(1);
        });

        it('should accept large page numbers', () => {
            const result = parsePaginationParams(makeUrl({ page: '9999' }));
            expect(result.page).toBe(9999);
        });
    });

    describe('parsing pageSize parameter', () => {
        it('should parse a valid pageSize from URL', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '10' }));
            expect(result.pageSize).toBe(10);
        });

        it('should clamp pageSize to minimum of 1 when zero is provided', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '0' }));
            expect(result.pageSize).toBe(1);
        });

        it('should clamp pageSize to minimum of 1 when negative value is provided', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '-10' }));
            expect(result.pageSize).toBe(1);
        });

        it('should clamp pageSize to default maxPageSize of 100', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '150' }));
            expect(result.pageSize).toBe(100);
        });

        it('should accept pageSize at exactly the maxPageSize boundary', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '100' }));
            expect(result.pageSize).toBe(100);
        });

        it('should clamp pageSize to custom maxPageSize', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '60' }), { maxPageSize: 50 });
            expect(result.pageSize).toBe(50);
        });

        it('should fall back to default pageSize when NaN is provided', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: 'xyz' }));
            expect(result.pageSize).toBe(25);
        });

        it('should fall back to custom default pageSize when NaN is provided', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: 'xyz' }), { pageSize: 15 });
            expect(result.pageSize).toBe(15);
        });

        it('should accept pageSize=1 as a valid boundary value', () => {
            const result = parsePaginationParams(makeUrl({ pageSize: '1' }));
            expect(result.pageSize).toBe(1);
        });
    });

    describe('combined parameters', () => {
        it('should parse both page and pageSize from URL', () => {
            const result = parsePaginationParams(makeUrl({ page: '3', pageSize: '50' }));
            expect(result).toEqual({ page: 3, pageSize: 50 });
        });

        it('should handle both parameters being invalid', () => {
            const result = parsePaginationParams(makeUrl({ page: 'bad', pageSize: 'bad' }));
            expect(result).toEqual({ page: 1, pageSize: 25 });
        });

        it('should handle both parameters needing clamping', () => {
            const result = parsePaginationParams(makeUrl({ page: '-1', pageSize: '500' }));
            expect(result).toEqual({ page: 1, pageSize: 100 });
        });
    });
});
