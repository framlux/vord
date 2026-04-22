// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';

const { mockGetTheme, mockSetTheme } = vi.hoisted(() => {
    let theme = 'light';
    const mockGetTheme = vi.fn(() => theme);
    const mockSetTheme = vi.fn((value: string) => { theme = value; });

    return { mockGetTheme, mockSetTheme };
});

vi.mock('$lib/stores/theme.svelte', () => ({
    getTheme: mockGetTheme,
    setTheme: mockSetTheme
}));

vi.mock('$app/environment', () => ({
    browser: true
}));

import ThemeToggle from './ThemeToggle.svelte';

describe('ThemeToggle', () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mockGetTheme.mockReturnValue('light');
        document.cookie = '';
        document.documentElement.classList.remove('dark', 'light');
    });

    it('should render toggle button with correct aria label', () => {
        render(ThemeToggle);

        expect(screen.getByLabelText('Toggle theme')).toBeInTheDocument();
    });

    it('should show Moon icon when theme is light', () => {
        mockGetTheme.mockReturnValue('light');
        const { container } = render(ThemeToggle);

        const moonIcon = container.querySelector('.lucide-moon');
        expect(moonIcon).not.toBeNull();
    });

    it('should show Sun icon when theme is dark', () => {
        mockGetTheme.mockReturnValue('dark');
        const { container } = render(ThemeToggle);

        const sunIcon = container.querySelector('.lucide-sun');
        expect(sunIcon).not.toBeNull();
    });

    it('should call setTheme with dark when toggling from light', async () => {
        mockGetTheme.mockReturnValue('light');
        render(ThemeToggle);

        await fireEvent.click(screen.getByLabelText('Toggle theme'));

        expect(mockSetTheme).toHaveBeenCalledWith('dark');
    });

    it('should call setTheme with light when toggling from dark', async () => {
        mockGetTheme.mockReturnValue('dark');
        render(ThemeToggle);

        await fireEvent.click(screen.getByLabelText('Toggle theme'));

        expect(mockSetTheme).toHaveBeenCalledWith('light');
    });

    it('should add dark class to document element when toggling to dark', async () => {
        mockGetTheme.mockReturnValue('light');
        render(ThemeToggle);

        await fireEvent.click(screen.getByLabelText('Toggle theme'));

        expect(document.documentElement.classList.contains('dark')).toBe(true);
        expect(document.documentElement.classList.contains('light')).toBe(false);
    });

    it('should add light class to document element when toggling to light', async () => {
        mockGetTheme.mockReturnValue('dark');
        render(ThemeToggle);

        await fireEvent.click(screen.getByLabelText('Toggle theme'));

        expect(document.documentElement.classList.contains('light')).toBe(true);
        expect(document.documentElement.classList.contains('dark')).toBe(false);
    });

    it('should set theme cookie when toggling', async () => {
        mockGetTheme.mockReturnValue('light');
        render(ThemeToggle);

        await fireEvent.click(screen.getByLabelText('Toggle theme'));

        expect(document.cookie).toContain('framlux_theme=dark');
    });

    it('should read stored theme from cookie on mount', () => {
        Object.defineProperty(document, 'cookie', {
            value: 'other=value; framlux_theme=dark; another=thing',
            writable: true,
            configurable: true
        });

        render(ThemeToggle);

        expect(mockSetTheme).toHaveBeenCalledWith('dark');
    });

    it('should default to light when no theme cookie exists', () => {
        Object.defineProperty(document, 'cookie', {
            value: '',
            writable: true,
            configurable: true
        });

        render(ThemeToggle);

        expect(mockSetTheme).toHaveBeenCalledWith('light');
    });
});
