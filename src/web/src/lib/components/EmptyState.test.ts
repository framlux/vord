// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import EmptyState from './EmptyState.svelte';

describe('EmptyState', () => {
    it('should render the default title when no title is provided', () => {
        render(EmptyState, { props: {} });
        expect(screen.getByText('No items found')).toBeInTheDocument();
    });

    it('should render a custom title when provided', () => {
        render(EmptyState, { props: { title: 'No machines available' } });
        expect(screen.getByText('No machines available')).toBeInTheDocument();
    });

    it('should render the title in an h3 element', () => {
        render(EmptyState, { props: { title: 'Empty' } });
        const heading = screen.getByRole('heading', { level: 3 });
        expect(heading).toHaveTextContent('Empty');
    });

    it('should render description when provided', () => {
        render(EmptyState, { props: { description: 'Try adjusting your filters' } });
        expect(screen.getByText('Try adjusting your filters')).toBeInTheDocument();
    });

    it('should not render description paragraph when description is empty', () => {
        const { container } = render(EmptyState, { props: {} });
        expect(container.querySelector('p')).toBeNull();
    });

    it('should not render description paragraph when description is an empty string', () => {
        const { container } = render(EmptyState, { props: { description: '' } });
        expect(container.querySelector('p')).toBeNull();
    });

    it('should render the InboxIcon', () => {
        const { container } = render(EmptyState, { props: {} });
        const svg = container.querySelector('svg');
        expect(svg).not.toBeNull();
    });
});
