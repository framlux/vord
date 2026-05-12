// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import PageHeader from './PageHeader.svelte';

describe('PageHeader', () => {
    it('should render the title in an h1 element', () => {
        render(PageHeader, { props: { title: 'Dashboard' } });
        const heading = screen.getByRole('heading', { level: 1 });
        expect(heading).toHaveTextContent('Dashboard');
    });

    it('should render the provided title text', () => {
        render(PageHeader, { props: { title: 'Machines' } });
        expect(screen.getByText('Machines')).toBeInTheDocument();
    });

    it('should render description when provided', () => {
        render(PageHeader, { props: { title: 'Settings', description: 'Manage your account' } });
        expect(screen.getByText('Manage your account')).toBeInTheDocument();
    });

    it('should not render description paragraph when description is empty', () => {
        const { container } = render(PageHeader, { props: { title: 'Settings' } });
        expect(container.querySelector('p')).toBeNull();
    });

    it('should not render description paragraph when description is an empty string', () => {
        const { container } = render(PageHeader, { props: { title: 'Settings', description: '' } });
        expect(container.querySelector('p')).toBeNull();
    });

    it('should render description in a p element', () => {
        const { container } = render(PageHeader, {
            props: { title: 'Alerts', description: 'Configure alert rules' }
        });
        const paragraph = container.querySelector('p');
        expect(paragraph).not.toBeNull();
        expect(paragraph?.textContent).toBe('Configure alert rules');
    });
});
