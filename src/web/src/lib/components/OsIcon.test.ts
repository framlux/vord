// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import OsIcon from './OsIcon.svelte';
import { OperatingSystem } from '$lib/api/types';

describe('OsIcon', () => {
    it('should render "Windows" for Windows enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.Windows } });
        expect(screen.getByText('Windows')).toBeInTheDocument();
    });

    it('should render "macOS" for MacOS enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.MacOS } });
        expect(screen.getByText('macOS')).toBeInTheDocument();
    });

    it('should render "Ubuntu" for Ubuntu enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.Ubuntu } });
        expect(screen.getByText('Ubuntu')).toBeInTheDocument();
    });

    it('should render "Fedora" for Fedora enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.Fedora } });
        expect(screen.getByText('Fedora')).toBeInTheDocument();
    });

    it('should render "Red Hat" for RedHat enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.RedHat } });
        expect(screen.getByText('Red Hat')).toBeInTheDocument();
    });

    it('should render "Unknown" for Unknown enum value', () => {
        render(OsIcon, { props: { os: OperatingSystem.Unknown } });
        expect(screen.getByText('Unknown')).toBeInTheDocument();
    });

    it('should set title attribute to the OS name', () => {
        render(OsIcon, { props: { os: OperatingSystem.Windows } });
        expect(screen.getByTitle('Windows')).toBeInTheDocument();
    });

    it('should fall back to "Unknown" for an unrecognized OS value', () => {
        render(OsIcon, { props: { os: 99 as OperatingSystem } });
        expect(screen.getByText('Unknown')).toBeInTheDocument();
        expect(screen.getByTitle('Unknown')).toBeInTheDocument();
    });

    it('should render the Monitor icon (SVG element)', () => {
        const { container } = render(OsIcon, { props: { os: OperatingSystem.Ubuntu } });
        const svg = container.querySelector('svg');
        expect(svg).not.toBeNull();
    });
});
