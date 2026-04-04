// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import InstallScriptModal from './InstallScriptModal.svelte';

describe('InstallScriptModal', () => {
    it('should not render when open is false', () => {
        const { container } = render(InstallScriptModal, { props: { open: false } });
        expect(container.querySelector('.fixed')).toBeNull();
    });

    it('should render modal with dialog role when open is true', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token-123'
            }
        });
        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('should display the Install Script title', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token-123'
            }
        });
        expect(screen.getByText('Install Script')).toBeInTheDocument();
    });

    it('should display a code block containing the token value in the script', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'my-unique-token-xyz'
            }
        });
        const codeBlock = screen.getByRole('dialog').querySelector('code');
        expect(codeBlock).not.toBeNull();
        expect(codeBlock?.textContent).toContain('my-unique-token-xyz');
    });

    it('should have a Copy Script button', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token'
            }
        });
        expect(screen.getByText('Copy Script')).toBeInTheDocument();
    });

    it('should have a Close button', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token'
            }
        });
        expect(screen.getByText('Close')).toBeInTheDocument();
    });

    it('should call onclose when Close button is clicked', async () => {
        const onclose = vi.fn();
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token',
                onclose
            }
        });

        await fireEvent.click(screen.getByText('Close'));
        expect(onclose).toHaveBeenCalledOnce();
    });

    it('should call onclose when X button is clicked', async () => {
        const onclose = vi.fn();
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token',
                onclose
            }
        });

        await fireEvent.click(screen.getByLabelText('Close'));
        expect(onclose).toHaveBeenCalledOnce();
    });

    it('should copy script to clipboard when Copy Script button is clicked', async () => {
        const writeText = vi.fn().mockResolvedValue(undefined);
        Object.assign(navigator, {
            clipboard: { writeText }
        });

        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'clipboard-test-token'
            }
        });

        await fireEvent.click(screen.getByText('Copy Script'));
        expect(writeText).toHaveBeenCalledOnce();
        const copiedText = writeText.mock.calls[0][0];
        expect(copiedText).toContain('clipboard-test-token');
        expect(copiedText).toContain('#!/usr/bin/env bash');
    });

    it('should have correct aria attributes for accessibility', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token'
            }
        });
        const dialog = screen.getByRole('dialog');
        expect(dialog).toHaveAttribute('aria-modal', 'true');
        expect(dialog).toHaveAttribute('aria-labelledby', 'install-script-dialog-title');
    });

    it('should remove dialog from DOM when open changes to false', () => {
        const { container, rerender } = render(InstallScriptModal, {
            props: { open: true, token: 'test-token' }
        });
        expect(screen.getByRole('dialog')).toBeInTheDocument();

        rerender({ open: false });
        expect(container.querySelector('.fixed')).toBeNull();
    });

    it('should not throw when Close is clicked without onclose handler', async () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token'
            }
        });
        await fireEvent.click(screen.getByText('Close'));
    });

    it('should include the default server address in the script', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token'
            }
        });
        const codeBlock = screen.getByRole('dialog').querySelector('code');
        expect(codeBlock?.textContent).toContain('grpc.vordfleet.dev');
    });

    it('should include a custom server address when provided', () => {
        render(InstallScriptModal, {
            props: {
                open: true,
                token: 'test-token',
                serverAddress: 'custom.server.io'
            }
        });
        const codeBlock = screen.getByRole('dialog').querySelector('code');
        expect(codeBlock?.textContent).toContain('custom.server.io');
    });
});
