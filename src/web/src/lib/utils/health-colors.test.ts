// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { MachineHealthStatus } from '$lib/api/types';
import { getHealthColors, getVitalColor, getVitalSeverity } from './health-colors';

describe('getHealthColors', () => {
    it('should return healthy colors with label "Healthy" for Healthy status', () => {
        const colors = getHealthColors(MachineHealthStatus.Healthy);
        expect(colors.label).toBe('Healthy');
        expect(colors.hex).toBe('#10B981');
    });

    it('should return warning colors with label "Warning" for Warning status', () => {
        const colors = getHealthColors(MachineHealthStatus.Warning);
        expect(colors.label).toBe('Warning');
        expect(colors.hex).toBe('#FBBF24');
    });

    it('should return critical colors with label "Critical" for Critical status', () => {
        const colors = getHealthColors(MachineHealthStatus.Critical);
        expect(colors.label).toBe('Critical');
        expect(colors.hex).toBe('#FF6467');
    });

    it('should return offline colors with label "Offline" for Offline status', () => {
        const colors = getHealthColors(MachineHealthStatus.Offline);
        expect(colors.label).toBe('Offline');
        expect(colors.hex).toBe('#6E6E77');
    });

    it('should return offline colors for an unknown status value', () => {
        const colors = getHealthColors(999 as MachineHealthStatus);
        expect(colors.label).toBe('Offline');
        expect(colors.hex).toBe('#6E6E77');
    });

    it('should include all expected properties in the returned color set', () => {
        const colors = getHealthColors(MachineHealthStatus.Healthy);
        expect(colors).toHaveProperty('hex');
        expect(colors).toHaveProperty('hexMuted');
        expect(colors).toHaveProperty('bg');
        expect(colors).toHaveProperty('text');
        expect(colors).toHaveProperty('dot');
        expect(colors).toHaveProperty('label');
    });

    it('should return muted hex with alpha suffix for each status', () => {
        expect(getHealthColors(MachineHealthStatus.Healthy).hexMuted).toBe('#10B98133');
        expect(getHealthColors(MachineHealthStatus.Warning).hexMuted).toBe('#FBBF2433');
        expect(getHealthColors(MachineHealthStatus.Critical).hexMuted).toBe('#FF646733');
        expect(getHealthColors(MachineHealthStatus.Offline).hexMuted).toBe('#6E6E7733');
    });
});

describe('getVitalColor', () => {
    it('should return green for values below 80', () => {
        expect(getVitalColor(0)).toBe('#10B981');
        expect(getVitalColor(50)).toBe('#10B981');
        expect(getVitalColor(79)).toBe('#10B981');
        expect(getVitalColor(79.9)).toBe('#10B981');
    });

    it('should return amber at exactly 80 percent', () => {
        expect(getVitalColor(80)).toBe('#FBBF24');
    });

    it('should return amber for values between 80 and 94', () => {
        expect(getVitalColor(85)).toBe('#FBBF24');
        expect(getVitalColor(94)).toBe('#FBBF24');
        expect(getVitalColor(94.9)).toBe('#FBBF24');
    });

    it('should return red at exactly 95 percent', () => {
        expect(getVitalColor(95)).toBe('#FF6467');
    });

    it('should return red for values above 95', () => {
        expect(getVitalColor(96)).toBe('#FF6467');
        expect(getVitalColor(100)).toBe('#FF6467');
    });

    it('should return green for negative values', () => {
        expect(getVitalColor(-1)).toBe('#10B981');
    });

    it('should return green for zero', () => {
        expect(getVitalColor(0)).toBe('#10B981');
    });
});

describe('getVitalSeverity', () => {
    it('should return "normal" for values below 80', () => {
        expect(getVitalSeverity(0)).toBe('normal');
        expect(getVitalSeverity(50)).toBe('normal');
        expect(getVitalSeverity(79)).toBe('normal');
        expect(getVitalSeverity(79.9)).toBe('normal');
    });

    it('should return "warning" at exactly 80 percent', () => {
        expect(getVitalSeverity(80)).toBe('warning');
    });

    it('should return "warning" for values between 80 and 94', () => {
        expect(getVitalSeverity(85)).toBe('warning');
        expect(getVitalSeverity(94)).toBe('warning');
        expect(getVitalSeverity(94.9)).toBe('warning');
    });

    it('should return "critical" at exactly 95 percent', () => {
        expect(getVitalSeverity(95)).toBe('critical');
    });

    it('should return "critical" for values above 95', () => {
        expect(getVitalSeverity(96)).toBe('critical');
        expect(getVitalSeverity(100)).toBe('critical');
    });

    it('should return "normal" for negative values', () => {
        expect(getVitalSeverity(-1)).toBe('normal');
    });

    it('should return "normal" for zero', () => {
        expect(getVitalSeverity(0)).toBe('normal');
    });
});
