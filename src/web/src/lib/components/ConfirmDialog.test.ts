// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import ConfirmDialog from './ConfirmDialog.svelte';

describe('ConfirmDialog', () => {
    it('should not render when open is false', () => {
        const { container } = render(ConfirmDialog, { props: { open: false } });
        expect(container.querySelector('.fixed')).toBeNull();
    });

    it('should render with default labels when open is true', () => {
        render(ConfirmDialog, { props: { open: true } });
        expect(screen.getByText('Are you sure?')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Confirm' })).toBeInTheDocument();
        expect(screen.getByText('Cancel')).toBeInTheDocument();
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('should display custom title, message, and button labels', () => {
        render(ConfirmDialog, {
            props: {
                open: true,
                title: 'Delete Machine',
                message: 'This action cannot be undone.',
                confirmLabel: 'Yes, delete',
                cancelLabel: 'No, keep'
            }
        });
        expect(screen.getByText('Delete Machine')).toBeInTheDocument();
        expect(screen.getByText('This action cannot be undone.')).toBeInTheDocument();
        expect(screen.getByText('Yes, delete')).toBeInTheDocument();
        expect(screen.getByText('No, keep')).toBeInTheDocument();
    });

    it('should call onconfirm when confirm button is clicked', async () => {
        const onconfirm = vi.fn();
        render(ConfirmDialog, {
            props: {
                open: true,
                onconfirm
            }
        });

        await fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
        expect(onconfirm).toHaveBeenCalledOnce();
    });

    it('should call oncancel when cancel button is clicked', async () => {
        const oncancel = vi.fn();
        render(ConfirmDialog, {
            props: {
                open: true,
                oncancel
            }
        });

        await fireEvent.click(screen.getByText('Cancel'));
        expect(oncancel).toHaveBeenCalledOnce();
    });

    it('should apply danger variant styling by default', () => {
        render(ConfirmDialog, { props: { open: true } });
        const confirmBtn = screen.getByRole('button', { name: 'Confirm' });
        expect(confirmBtn.className).toContain('bg-error-500');
    });

    it('should apply warning variant styling', () => {
        render(ConfirmDialog, { props: { open: true, variant: 'warning' } });
        const confirmBtn = screen.getByRole('button', { name: 'Confirm' });
        expect(confirmBtn.className).toContain('bg-warning-500');
    });

    it('should apply info variant styling', () => {
        render(ConfirmDialog, { props: { open: true, variant: 'info' } });
        const confirmBtn = screen.getByRole('button', { name: 'Confirm' });
        expect(confirmBtn.className).toContain('bg-primary-500');
    });

    it('should not throw when confirm is clicked without onconfirm handler', async () => {
        render(ConfirmDialog, { props: { open: true } });
        await fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    });

    it('should not throw when cancel is clicked without oncancel handler', async () => {
        render(ConfirmDialog, { props: { open: true } });
        await fireEvent.click(screen.getByText('Cancel'));
    });

    it('should remove dialog from DOM when open changes to false', () => {
        const { container, rerender } = render(ConfirmDialog, { props: { open: true } });
        expect(screen.getByRole('dialog')).toBeInTheDocument();

        rerender({ open: false });
        expect(container.querySelector('.fixed')).toBeNull();
    });

    it('should have correct aria attributes for accessibility', () => {
        render(ConfirmDialog, {
            props: {
                open: true,
                title: 'Confirm Delete'
            }
        });
        const dialog = screen.getByRole('dialog');
        expect(dialog).toHaveAttribute('aria-modal', 'true');
        expect(dialog).toHaveAttribute('aria-labelledby', 'confirm-dialog-title');
    });
});
