// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import Pagination from './Pagination.svelte';

describe('Pagination', () => {
    it('should not render when totalPages is 1', () => {
        const { container } = render(Pagination, { props: { page: 1, totalPages: 1 } });
        expect(container.querySelector('div')).toBeNull();
    });

    it('should not render when totalPages is omitted (defaults to 1)', () => {
        const { container } = render(Pagination, { props: {} });
        expect(container.querySelector('div')).toBeNull();
    });

    it('should render when totalPages is greater than 1', () => {
        render(Pagination, { props: { page: 1, totalPages: 5 } });
        expect(screen.getByText('Page 1 of 5')).toBeInTheDocument();
    });

    it('should display correct page and totalPages text', () => {
        render(Pagination, { props: { page: 3, totalPages: 10 } });
        expect(screen.getByText('Page 3 of 10')).toBeInTheDocument();
    });

    it('should disable Previous button on first page', () => {
        render(Pagination, { props: { page: 1, totalPages: 5 } });
        const prevButton = screen.getByLabelText('Previous page');
        expect(prevButton).toBeDisabled();
    });

    it('should disable Next button on last page', () => {
        render(Pagination, { props: { page: 5, totalPages: 5 } });
        const nextButton = screen.getByLabelText('Next page');
        expect(nextButton).toBeDisabled();
    });

    it('should enable both buttons on a middle page', () => {
        render(Pagination, { props: { page: 3, totalPages: 5 } });
        const prevButton = screen.getByLabelText('Previous page');
        const nextButton = screen.getByLabelText('Next page');
        expect(prevButton).not.toBeDisabled();
        expect(nextButton).not.toBeDisabled();
    });

    it('should call onchange with page - 1 when Previous is clicked', async () => {
        const onchange = vi.fn();
        render(Pagination, { props: { page: 3, totalPages: 5, onchange } });
        const prevButton = screen.getByLabelText('Previous page');
        await fireEvent.click(prevButton);
        expect(onchange).toHaveBeenCalledWith(2);
    });

    it('should call onchange with page + 1 when Next is clicked', async () => {
        const onchange = vi.fn();
        render(Pagination, { props: { page: 3, totalPages: 5, onchange } });
        const nextButton = screen.getByLabelText('Next page');
        await fireEvent.click(nextButton);
        expect(onchange).toHaveBeenCalledWith(4);
    });

    it('should not throw when onchange is not provided and buttons are clicked', async () => {
        render(Pagination, { props: { page: 2, totalPages: 5 } });
        const prevButton = screen.getByLabelText('Previous page');
        const nextButton = screen.getByLabelText('Next page');
        await expect(fireEvent.click(prevButton)).resolves.not.toThrow();
        await expect(fireEvent.click(nextButton)).resolves.not.toThrow();
    });
});
