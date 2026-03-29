// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import HealthBadge from './HealthBadge.svelte';
import { MachineHealthStatus } from '$lib/api/types';

describe('HealthBadge', () => {
    it('should render Healthy label for Healthy status', () => {
        render(HealthBadge, { props: { status: MachineHealthStatus.Healthy } });
        expect(screen.getByText('Healthy')).toBeDefined();
    });

    it('should render Warning label for Warning status', () => {
        render(HealthBadge, { props: { status: MachineHealthStatus.Warning } });
        expect(screen.getByText('Warning')).toBeDefined();
    });

    it('should render Critical label for Critical status', () => {
        render(HealthBadge, { props: { status: MachineHealthStatus.Critical } });
        expect(screen.getByText('Critical')).toBeDefined();
    });

    it('should render Offline label for Offline status', () => {
        render(HealthBadge, { props: { status: MachineHealthStatus.Offline } });
        expect(screen.getByText('Offline')).toBeDefined();
    });

    it('should apply green classes for Healthy status', () => {
        const { container } = render(HealthBadge, { props: { status: MachineHealthStatus.Healthy } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-green-100');
    });

    it('should apply amber classes for Warning status', () => {
        const { container } = render(HealthBadge, { props: { status: MachineHealthStatus.Warning } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-amber-100');
    });

    it('should apply red classes for Critical status', () => {
        const { container } = render(HealthBadge, { props: { status: MachineHealthStatus.Critical } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-red-100');
    });

    it('should apply gray classes for Offline status', () => {
        const { container } = render(HealthBadge, { props: { status: MachineHealthStatus.Offline } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-gray-100');
    });

    it('should fall back to Offline config for unknown status value', () => {
        const { container } = render(HealthBadge, { props: { status: 99 as MachineHealthStatus } });
        const badge = container.querySelector('span');
        expect(badge?.className).toContain('bg-gray-100');
        expect(screen.getByText('Offline')).toBeDefined();
    });
});
