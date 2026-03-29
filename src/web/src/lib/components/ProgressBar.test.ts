// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import ProgressBar from './ProgressBar.svelte';

describe('ProgressBar', () => {
    it('should display percentage text', () => {
        render(ProgressBar, { props: { value: 50, max: 100 } });
        expect(screen.getByText('50%')).toBeDefined();
    });

    it('should calculate percentage correctly', () => {
        render(ProgressBar, { props: { value: 75, max: 100 } });
        expect(screen.getByText('75%')).toBeDefined();
    });

    it('should cap at 100%', () => {
        render(ProgressBar, { props: { value: 150, max: 100 } });
        expect(screen.getByText('100%')).toBeDefined();
    });

    it('should show 0% when max is 0', () => {
        render(ProgressBar, { props: { value: 50, max: 0 } });
        expect(screen.getByText('0%')).toBeDefined();
    });

    it('should default max to 100', () => {
        render(ProgressBar, { props: { value: 42 } });
        expect(screen.getByText('42%')).toBeDefined();
    });

    it('should display label when provided', () => {
        render(ProgressBar, { props: { value: 50, max: 100, label: 'CPU' } });
        expect(screen.getByText('CPU')).toBeDefined();
    });

    it('should not display label when not provided', () => {
        const { container } = render(ProgressBar, { props: { value: 50 } });
        const spans = container.querySelectorAll('span');
        // Should only have the percentage span, no label span
        expect(spans).toHaveLength(1);
        expect(spans[0].textContent?.trim()).toBe('50%');
    });

    it('should apply green color for low usage', () => {
        const { container } = render(ProgressBar, { props: { value: 30 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-green-500');
    });

    it('should apply amber color for high usage (80-94%)', () => {
        const { container } = render(ProgressBar, { props: { value: 85 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-amber-500');
    });

    it('should apply red color for critical usage (95%+)', () => {
        const { container } = render(ProgressBar, { props: { value: 97 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-red-500');
    });

    it('should set width style based on percentage', () => {
        const { container } = render(ProgressBar, { props: { value: 60 } });
        const bar = container.querySelector('[style]');
        expect(bar?.getAttribute('style')).toContain('width: 60%');
    });

    it('should apply amber at exactly warning threshold (80%)', () => {
        const { container } = render(ProgressBar, { props: { value: 80 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-amber-500');
    });

    it('should apply green just below warning threshold (79%)', () => {
        const { container } = render(ProgressBar, { props: { value: 79 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-green-500');
    });

    it('should apply red at exactly critical threshold (95%)', () => {
        const { container } = render(ProgressBar, { props: { value: 95 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-red-500');
    });

    it('should apply amber just below critical threshold (94%)', () => {
        const { container } = render(ProgressBar, { props: { value: 94 } });
        const bar = container.querySelector('[style]');
        expect(bar?.className).toContain('bg-amber-500');
    });

    it('should clamp negative values to 0%', () => {
        render(ProgressBar, { props: { value: -10 } });
        expect(screen.getByText('0%')).toBeDefined();
    });
});
