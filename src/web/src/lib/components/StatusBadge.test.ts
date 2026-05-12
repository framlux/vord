// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';

vi.mock('$lib/utils/format', () => ({
    formatRelativeTime: vi.fn(() => '5m ago')
}));

import StatusBadge from './StatusBadge.svelte';

describe('StatusBadge', () => {
    it('should render "Online" when online is true', () => {
        render(StatusBadge, { props: { online: true } });
        expect(screen.getByText('Online')).toBeInTheDocument();
    });

    it('should render "Offline" when online is false', () => {
        render(StatusBadge, { props: { online: false } });
        expect(screen.getByText('Offline')).toBeInTheDocument();
    });

    it('should have aria-label "Status: Online" when online', () => {
        render(StatusBadge, { props: { online: true } });
        expect(screen.getByLabelText('Status: Online')).toBeInTheDocument();
    });

    it('should have aria-label "Status: Offline" when offline', () => {
        render(StatusBadge, { props: { online: false } });
        expect(screen.getByLabelText('Status: Offline')).toBeInTheDocument();
    });

    it('should show relative time when lastPing is provided', () => {
        render(StatusBadge, { props: { online: true, lastPing: '2026-01-01T00:00:00Z' } });
        expect(screen.getByText('(5m ago)')).toBeInTheDocument();
    });

    it('should not show relative time when lastPing is null', () => {
        render(StatusBadge, { props: { online: true, lastPing: null } });
        expect(screen.queryByText('(5m ago)')).not.toBeInTheDocument();
    });

    it('should not show relative time when lastPing is not provided', () => {
        render(StatusBadge, { props: { online: false } });
        expect(screen.queryByText('(5m ago)')).not.toBeInTheDocument();
    });

    it('should apply green styling when online', () => {
        const { container } = render(StatusBadge, { props: { online: true } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-green-100');
    });

    it('should apply red styling when offline', () => {
        const { container } = render(StatusBadge, { props: { online: false } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-red-100');
    });
});
