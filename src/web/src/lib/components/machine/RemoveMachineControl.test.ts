// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';

const { deleteMachine, goto } = vi.hoisted(() => ({ deleteMachine: vi.fn(), goto: vi.fn() }));

vi.mock('$app/navigation', () => ({ goto }));

vi.mock('$lib/api/client', () => {
	class ApiError extends Error {
		constructor(public status: number, message: string) {
			super(message);
			this.name = 'ApiError';
		}
	}

	return {
		ApiError,
		ApiClient: class {
			deleteMachine = deleteMachine;
		}
	};
});

import { ApiError } from '$lib/api/client';
import RemoveMachineControl from './RemoveMachineControl.svelte';

function renderControl() {
	return render(RemoveMachineControl, { props: { machineId: 7, machineName: 'web-01', csrfToken: 'csrf' } });
}

describe('RemoveMachineControl', () => {
	beforeEach(() => {
		deleteMachine.mockReset();
		goto.mockReset();
	});

	it('asks before removing, and tells the user to uninstall the agent first', async () => {
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));

		expect(screen.getByText(/Uninstall the agent from web-01 first/)).toBeInTheDocument();
		expect(screen.getByText('Removing a host deletes it from your fleet. It no longer appears in your machine list or dashboard.')).toBeInTheDocument();
		expect(screen.getByRole('link', { name: 'How to uninstall the agent' })).toHaveAttribute(
			'href',
			'https://vordfleet.dev/support/fleet-management/agent-troubleshooting'
		);
		expect(deleteMachine).not.toHaveBeenCalled();
	});

	it('removes nothing when the user backs out', async () => {
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'No' }));

		expect(deleteMachine).not.toHaveBeenCalled();
		expect(screen.getByRole('button', { name: 'Remove host' })).toBeInTheDocument();
	});

	it('removes the host and returns to the machine list on confirmation', async () => {
		deleteMachine.mockResolvedValue(undefined);
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));

		await waitFor(() => expect(goto).toHaveBeenCalledWith('/machines', { invalidateAll: true }));
		expect(deleteMachine).toHaveBeenCalledWith(7);
	});

	it('sends one delete however many times confirmation is clicked', async () => {
		let resolve: () => void = () => {};
		deleteMachine.mockReturnValue(new Promise<void>((r) => (resolve = r)));
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		const confirm = screen.getByRole('button', { name: 'Yes, remove' });
		await fireEvent.click(confirm);
		await fireEvent.click(confirm);
		resolve();

		await waitFor(() => expect(goto).toHaveBeenCalledTimes(1));
		expect(deleteMachine).toHaveBeenCalledTimes(1);
	});

	it('does not offer the way back to the list once the user backs out of a failed removal and fails differently', async () => {
		deleteMachine.mockRejectedValueOnce(new ApiError(404, 'Not found'));
		deleteMachine.mockRejectedValueOnce(new Error('Network down'));
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));
		expect(await screen.findByRole('link', { name: 'Back to machines' })).toBeInTheDocument();

		await fireEvent.click(screen.getByRole('button', { name: 'No' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));

		expect(await screen.findByRole('alert')).toHaveTextContent('Network down');
		expect(screen.queryByRole('link', { name: 'Back to machines' })).not.toBeInTheDocument();
	});

	it('does not offer the way back to the list when a retry fails differently without backing out', async () => {
		deleteMachine.mockRejectedValueOnce(new ApiError(404, 'Not found'));
		deleteMachine.mockRejectedValueOnce(new Error('Network down'));
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));
		expect(await screen.findByRole('link', { name: 'Back to machines' })).toBeInTheDocument();

		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));

		await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Network down'));
		expect(screen.queryByRole('link', { name: 'Back to machines' })).not.toBeInTheDocument();
	});
});
