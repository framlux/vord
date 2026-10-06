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

	it("shows the server's reason when the tenant may not make changes", async () => {
		deleteMachine.mockRejectedValue(new ApiError(403, 'Your subscription is canceled; changes are read-only.'));
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));

		expect(await screen.findByRole('alert')).toHaveTextContent('Your subscription is canceled; changes are read-only.');
		expect(goto).not.toHaveBeenCalled();
	});

	it('says the host is already gone when another tab removed it', async () => {
		deleteMachine.mockRejectedValue(new ApiError(404, 'Machine not found'));
		renderControl();

		await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));

		expect(await screen.findByRole('alert')).toHaveTextContent('This host has already been removed.');
		expect(screen.getByRole('link', { name: 'Back to machines' })).toHaveAttribute('href', '/machines');
	});
});
