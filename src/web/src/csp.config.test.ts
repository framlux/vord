// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// svelte.config.js pulls in vite and esbuild, which refuse to load under jsdom's TextEncoder.
// @vitest-environment node

import { readFileSync } from 'node:fs';
import { describe, it, expect } from 'vitest';
import config from '../svelte.config.js';

// SvelteKit boots every page with an inline <script>. A policy that allows scripts only from
// 'self' refuses that script, so the page never hydrates and every client-side handler is dead.
// kit.csp makes SvelteKit stamp its own inline scripts with a per-response nonce, which lets the
// policy stay strict. These cases pin the intent of that configuration.
const csp = config.kit?.csp;
const directives = csp?.directives ?? {};

describe('svelte.config kit.csp', () => {
    it('lets SvelteKit choose a nonce per dynamically rendered response', () => {
        expect(csp?.mode).toBe('auto');
    });

    it('allows scripts from the same origin only, with no inline or eval escape hatch', () => {
        expect(directives['script-src']).toEqual(['self']);
        expect(directives['script-src']).not.toContain('unsafe-inline');
        expect(directives['script-src']).not.toContain('unsafe-eval');
    });

    it('does not allow unsafe-inline or unsafe-eval in any script directive', () => {
        for (const name of ['default-src', 'script-src', 'script-src-elem', 'script-src-attr'] as const) {
            const sources: readonly string[] = directives[name] ?? [];

            expect(sources, name).not.toContain('unsafe-inline');
            expect(sources, name).not.toContain('unsafe-eval');
        }
    });

    it('preserves the restrictive posture the hand-written policy had', () => {
        expect(directives['default-src']).toEqual(['self']);
        expect(directives['base-uri']).toEqual(['self']);
        expect(directives['frame-ancestors']).toEqual(['none']);
        expect(directives['img-src']).toEqual(['self', 'data:']);
        expect(directives['style-src']).toEqual(['self', 'unsafe-inline', 'https://fonts.googleapis.com']);
        expect(directives['font-src']).toEqual(['self', 'https://fonts.gstatic.com']);
        expect(directives['connect-src']).toEqual(['self']);
        expect(directives['object-src']).toEqual(['none']);
    });

    it('adds no external script origin', () => {
        const scriptSources: readonly string[] = directives['script-src'] ?? [];

        expect(scriptSources.filter((source) => /^[a-z]+:\/\/|^\*/.test(source))).toEqual([]);
    });
});

// SvelteKit stamps its own scripts, but a script written into src/app.html is invisible to it and
// is blocked unless the template carries the nonce placeholder SvelteKit substitutes per response.
describe('app.html inline scripts', () => {
    const template = readFileSync(new URL('./app.html', import.meta.url), 'utf8');
    const scriptTags = template.match(/<script\b[^>]*>/g) ?? [];

    it('contains the theme script this guard exists for', () => {
        expect(scriptTags.length).toBeGreaterThan(0);
    });

    it.each(scriptTags)('carries the SvelteKit nonce placeholder on %s', (tag) => {
        expect(tag).toContain('nonce="%sveltekit.nonce%"');
    });
});
