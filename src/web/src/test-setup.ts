// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { vi } from 'vitest';

// jsdom doesn't implement matchMedia; provide a default stub so components
// that check `prefers-color-scheme` (e.g. ThemeToggle) can run in tests.
// Individual tests can override via `Object.defineProperty(window, 'matchMedia', ...)`.
if (typeof window !== 'undefined' && window.matchMedia === undefined) {
    Object.defineProperty(window, 'matchMedia', {
        writable: true,
        configurable: true,
        value: (query: string) => ({
            matches: false,
            media: query,
            onchange: null,
            addListener: vi.fn(),
            removeListener: vi.fn(),
            addEventListener: vi.fn(),
            removeEventListener: vi.fn(),
            dispatchEvent: vi.fn()
        })
    });
}
