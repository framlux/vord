// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { OperatingSystem, MachineType } from '$lib/api/types';
import { getOsName, getTypeName, getOsFilterOptions, getTypeFilterOptions } from './enums';

describe('getOsName', () => {
    it('should return "Unknown" for OperatingSystem.Unknown', () => {
        expect(getOsName(OperatingSystem.Unknown)).toBe('Unknown');
    });

    it('should return "Windows" for OperatingSystem.Windows', () => {
        expect(getOsName(OperatingSystem.Windows)).toBe('Windows');
    });

    it('should return "MacOS" for OperatingSystem.MacOS', () => {
        expect(getOsName(OperatingSystem.MacOS)).toBe('MacOS');
    });

    it('should return "Ubuntu" for OperatingSystem.Ubuntu', () => {
        expect(getOsName(OperatingSystem.Ubuntu)).toBe('Ubuntu');
    });

    it('should return "Fedora" for OperatingSystem.Fedora', () => {
        expect(getOsName(OperatingSystem.Fedora)).toBe('Fedora');
    });

    it('should return "RedHat" for OperatingSystem.RedHat', () => {
        expect(getOsName(OperatingSystem.RedHat)).toBe('RedHat');
    });

    it('should return "Unknown" for an invalid enum value', () => {
        expect(getOsName(999 as OperatingSystem)).toBe('Unknown');
    });
});

describe('getTypeName', () => {
    it('should return "Unknown" for MachineType.Unknown', () => {
        expect(getTypeName(MachineType.Unknown)).toBe('Unknown');
    });

    it('should return "Desktop" for MachineType.Desktop', () => {
        expect(getTypeName(MachineType.Desktop)).toBe('Desktop');
    });

    it('should return "Laptop" for MachineType.Laptop', () => {
        expect(getTypeName(MachineType.Laptop)).toBe('Laptop');
    });

    it('should return "Bare Metal Server" for MachineType.BareMetalServer', () => {
        expect(getTypeName(MachineType.BareMetalServer)).toBe('Bare Metal Server');
    });

    it('should return "Virtual Machine" for MachineType.VirtualMachine', () => {
        expect(getTypeName(MachineType.VirtualMachine)).toBe('Virtual Machine');
    });

    it('should return "Unknown" for an invalid enum value', () => {
        expect(getTypeName(999 as MachineType)).toBe('Unknown');
    });
});

describe('getOsFilterOptions', () => {
    it('should include "All OS" as the first option', () => {
        const options = getOsFilterOptions();
        expect(options[0]).toEqual({ value: '', label: 'All OS' });
    });

    it('should contain all OperatingSystem enum members', () => {
        const options = getOsFilterOptions();
        const labels = options.map((o) => o.label);
        expect(labels).toContain('Unknown');
        expect(labels).toContain('Windows');
        expect(labels).toContain('MacOS');
        expect(labels).toContain('Ubuntu');
        expect(labels).toContain('Fedora');
        expect(labels).toContain('RedHat');
        expect(labels).toContain('Debian');
    });

    it('should have enum key names as values for non-All options', () => {
        const options = getOsFilterOptions();
        const values = options.slice(1).map((o) => o.value);
        expect(values).toContain('Unknown');
        expect(values).toContain('Windows');
        expect(values).toContain('MacOS');
        expect(values).toContain('Ubuntu');
        expect(values).toContain('Fedora');
        expect(values).toContain('RedHat');
        expect(values).toContain('Debian');
    });

    it('should have one more option than the number of enum members', () => {
        const options = getOsFilterOptions();
        // 7 enum members + 1 "All OS" entry
        expect(options.length).toBe(8);
    });
});

describe('getTypeFilterOptions', () => {
    it('should include "All Types" as the first option', () => {
        const options = getTypeFilterOptions();
        expect(options[0]).toEqual({ value: '', label: 'All Types' });
    });

    it('should contain all MachineType enum members', () => {
        const options = getTypeFilterOptions();
        const labels = options.map((o) => o.label);
        expect(labels).toContain('Unknown');
        expect(labels).toContain('Desktop');
        expect(labels).toContain('Laptop');
        expect(labels).toContain('Bare Metal Server');
        expect(labels).toContain('Virtual Machine');
    });

    it('should have enum key names as values for non-All options', () => {
        const options = getTypeFilterOptions();
        const values = options.slice(1).map((o) => o.value);
        expect(values).toContain('Unknown');
        expect(values).toContain('Desktop');
        expect(values).toContain('Laptop');
        expect(values).toContain('BareMetalServer');
        expect(values).toContain('VirtualMachine');
    });

    it('should have one more option than the number of enum members', () => {
        const options = getTypeFilterOptions();
        // 5 enum members + 1 "All Types" entry
        expect(options.length).toBe(6);
    });
});
