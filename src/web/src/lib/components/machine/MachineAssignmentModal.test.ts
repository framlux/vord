// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus, type FleetMachineDto, type PaginatedResponse } from '$lib/api/types';

const searchMachinesMock = vi.fn();
const getMachineIdsMock = vi.fn();

vi.mock('$lib/api/client', () => ({
	ApiClient: class {
		searchMachines = searchMachinesMock;
		getMachineIds = getMachineIdsMock;
	}
}));

import MachineAssignmentModal from './MachineAssignmentModal.svelte';

function makeMachine(id: number, name: string): FleetMachineDto {
	return {
		id,
		name,
		hostname: `${name}.lan`,
		ipAddress: '10.0.0.1',
		hardwareModel: null,
		healthStatus: MachineHealthStatus.Healthy,
		cpuUsagePercent: 10,
		memoryUsagePercent: 20,
		maxDiskUsagePercent: 30,
		hasDiskHealthIssue: false,
		hasHardwareIssue: false,
		isOnline: true,
		lastPing: null,
		pendingUpdates: 0,
		securityUpdates: 0,
		failedServices: 0,
		totalServices: 10
	};
}

function makePage(
	items: FleetMachineDto[],
	overrides: Partial<PaginatedResponse<FleetMachineDto>> = {}
): PaginatedResponse<FleetMachineDto> {
	return {
		items,
		page: 1,
		pageSize: 25,
		totalCount: items.length,
		totalPages: 1,
		hasNextPage: false,
		...overrides
	} as PaginatedResponse<FleetMachineDto>;
}

// Two pages of one machine each, so paging is exercised without a wall of fixtures.
const pageOne = makePage([makeMachine(1, 'web-01')], { page: 1, totalCount: 2, totalPages: 2, hasNextPage: true });
const pageTwo = makePage([makeMachine(2, 'db-01')], { page: 2, totalCount: 2, totalPages: 2, hasNextPage: false });

function respondByPage() {
	searchMachinesMock.mockImplementation(async (params: { page?: number }) =>
		(params?.page ?? 1) === 2 ? pageTwo : pageOne
	);
}

function baseProps(overrides: Record<string, unknown> = {}) {
	return {
		open: true,
		title: 'Machines watched by Disk usage above 90%',
		initialSelectedIds: [] as number[],
		allowEmpty: true,
		onsave: vi.fn(),
		oncancel: vi.fn(),
		...overrides
	};
}

beforeEach(() => {
	vi.clearAllMocks();
	respondByPage();
	getMachineIdsMock.mockResolvedValue({ ids: [1, 2], totalCount: 2, truncated: false });
});

describe('MachineAssignmentModal', () => {
	it('loads the first page when opened and lists the machines it can reach', async () => {
		render(MachineAssignmentModal, { props: baseProps() });

		expect(await screen.findByText('web-01')).toBeInTheDocument();
		// A page size the endpoint will actually serve. Asking for more is refused outright now,
		// not clamped, so a picker that guesses high renders nothing at all.
		expect(searchMachinesMock).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }));
	});

	// The requirement that drives the whole state model: the visible list is a window onto the
	// fleet, and the selection is independent of it.
	it('keeps a selection made on one page when the user pages away and back', async () => {
		render(MachineAssignmentModal, { props: baseProps() });

		const first = await screen.findByRole('checkbox', { name: /web-01/ });
		await fireEvent.click(first);
		expect(first).toBeChecked();

		await fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
		expect(await screen.findByText('db-01')).toBeInTheDocument();

		await fireEvent.click(screen.getByRole('button', { name: 'Previous page' }));
		expect(await screen.findByRole('checkbox', { name: /web-01/ })).toBeChecked();
	});

	it('keeps a selection when the filter changes to something that excludes it', async () => {
		render(MachineAssignmentModal, { props: baseProps() });

		await fireEvent.click(await screen.findByRole('checkbox', { name: /web-01/ }));

		searchMachinesMock.mockResolvedValue(makePage([makeMachine(2, 'db-01')]));
		await fireEvent.input(screen.getByLabelText(/search machines/i), { target: { value: 'db' } });

		await waitFor(() => expect(screen.getByText('db-01')).toBeInTheDocument());
		expect(screen.queryByText('web-01')).not.toBeInTheDocument();
		// Still counted, even though it is no longer drawn.
		expect(screen.getByText(/1 selected/i)).toBeInTheDocument();
	});

	it('tells the user how much of the selection is not on this page', async () => {
		render(MachineAssignmentModal, { props: baseProps({ initialSelectedIds: [2] }) });

		expect(await screen.findByText('web-01')).toBeInTheDocument();
		// Worded so it cannot be confused with the "N selected" counter, which is a different
		// number answering a different question.
		expect(screen.getByText(/1 machine you selected is not shown/i)).toBeInTheDocument();
	});

	it('resolves select-all-matching through the ids endpoint using the current filter', async () => {
		getMachineIdsMock.mockResolvedValue({ ids: [7, 8, 9], totalCount: 3, truncated: false });
		render(MachineAssignmentModal, { props: baseProps() });

		await fireEvent.input(await screen.findByLabelText(/search machines/i), { target: { value: 'prod' } });
		await fireEvent.click(screen.getByRole('button', { name: /select all matching/i }));

		await waitFor(() =>
			expect(getMachineIdsMock).toHaveBeenCalledWith(expect.objectContaining({ search: 'prod' }))
		);
		expect(screen.getByText(/3 selected/i)).toBeInTheDocument();
	});

	// The ids endpoint caps what it returns and says so. Presenting a capped answer as the whole
	// match is how a picker silently omits machines, which is the defect this modal exists to end.
	it('refuses to present a truncated select-all as the complete match', async () => {
		getMachineIdsMock.mockResolvedValue({ ids: [1, 2], totalCount: 12000, truncated: true });
		render(MachineAssignmentModal, { props: baseProps() });

		await fireEvent.click(await screen.findByRole('button', { name: /select all matching/i }));

		expect(await screen.findByText(/too many machines match/i)).toBeInTheDocument();
	});

	it('saves the whole selection, not just what was on screen', async () => {
		const onsave = vi.fn();
		render(MachineAssignmentModal, { props: baseProps({ initialSelectedIds: [99], onsave }) });

		await fireEvent.click(await screen.findByRole('checkbox', { name: /web-01/ }));
		await fireEvent.click(screen.getByRole('button', { name: /save/i }));

		const [ids] = onsave.mock.calls[0];
		expect([...ids].sort((a: number, b: number) => a - b)).toEqual([1, 99]);
	});

	// The API may only unassign machines the caller says it was choosing from. An initially
	// assigned machine is one the modal represents and the user can untick, so it must be offered
	// or unticking it would be silently carried through instead of removed.
	it('offers every id it could have unticked, so a removal is honoured', async () => {
		const onsave = vi.fn();
		render(MachineAssignmentModal, { props: baseProps({ initialSelectedIds: [99], onsave }) });

		await screen.findByText('web-01');
		await fireEvent.click(screen.getByRole('button', { name: /save/i }));

		const [, offeredIds] = onsave.mock.calls[0];
		expect(offeredIds).toEqual(expect.arrayContaining([1, 99]));
	});

	it('refuses an empty selection where the endpoint would refuse it', async () => {
		const onsave = vi.fn();
		render(MachineAssignmentModal, { props: baseProps({ allowEmpty: false, onsave }) });

		await screen.findByText('web-01');
		const save = screen.getByRole('button', { name: /save/i });

		expect(save).toBeDisabled();
		expect(screen.getByText(/at least one machine/i)).toBeInTheDocument();
		expect(onsave).not.toHaveBeenCalled();
	});

	it('allows an empty selection where it parks the rule rather than failing', async () => {
		const onsave = vi.fn();
		render(MachineAssignmentModal, { props: baseProps({ allowEmpty: true, onsave }) });

		await screen.findByText('web-01');
		await fireEvent.click(screen.getByRole('button', { name: /save/i }));

		expect(onsave).toHaveBeenCalled();
		expect(onsave.mock.calls[0][0]).toEqual([]);
	});

	it('surfaces a failed load instead of rendering an empty fleet', async () => {
		searchMachinesMock.mockRejectedValue(new Error('boom'));
		render(MachineAssignmentModal, { props: baseProps() });

		expect(await screen.findByRole('alert')).toHaveTextContent(/could not load machines/i);
	});

	describe('accessibility', () => {
		it('is a labelled modal dialog', async () => {
			render(MachineAssignmentModal, { props: baseProps() });

			const dialog = await screen.findByRole('dialog');
			expect(dialog).toHaveAttribute('aria-modal', 'true');
			expect(dialog).toHaveAccessibleName('Machines watched by Disk usage above 90%');
		});

		it('moves focus into the dialog when it opens', async () => {
			render(MachineAssignmentModal, { props: baseProps() });

			const dialog = await screen.findByRole('dialog');
			await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
		});

		it('closes on Escape', async () => {
			const oncancel = vi.fn();
			render(MachineAssignmentModal, { props: baseProps({ oncancel }) });

			await screen.findByRole('dialog');
			await fireEvent.keyDown(window, { key: 'Escape' });

			expect(oncancel).toHaveBeenCalled();
		});

		// These assert where focus LANDS. The previous version fired Tab and then checked that focus
		// was still somewhere inside the dialog, which jsdom guarantees on its own — it does not move
		// focus on Tab, so the element the test had just focused by hand was trivially still inside.
		// That assertion passed with trapFocus deleted entirely.
		it('wraps Tab on the last control back to the first', async () => {
			render(MachineAssignmentModal, { props: baseProps() });

			const dialog = await screen.findByRole('dialog');
			const focusable = dialog.querySelectorAll<HTMLElement>(
				'button:not([disabled]), input:not([disabled]), select:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'
			);
			const first = focusable[0];
			const last = focusable[focusable.length - 1];

			last.focus();
			await fireEvent.keyDown(dialog, { key: 'Tab' });

			expect(document.activeElement).toBe(first);
		});

		it('wraps Shift+Tab on the first control back to the last', async () => {
			render(MachineAssignmentModal, { props: baseProps() });

			const dialog = await screen.findByRole('dialog');
			const focusable = dialog.querySelectorAll<HTMLElement>(
				'button:not([disabled]), input:not([disabled]), select:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'
			);
			const first = focusable[0];
			const last = focusable[focusable.length - 1];

			first.focus();
			await fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true });

			expect(document.activeElement).toBe(last);
		});

		it('returns focus to whatever opened it', async () => {
			// Claimed in the commit that shipped this modal and never tested. Losing focus to the
			// document body on close strands a keyboard user at the top of the page, with no way back
			// to the row they were working on.
			const trigger = document.createElement('button');
			trigger.textContent = 'Assign machines';
			document.body.appendChild(trigger);
			trigger.focus();

			const { rerender } = render(MachineAssignmentModal, { props: baseProps() });
			await screen.findByRole('dialog');

			await rerender(baseProps({ open: false }));

			await waitFor(() => expect(document.activeElement).toBe(trigger));
			trigger.remove();
		});
	});

	it('saves a selection built across two pages, not merely the page in view', async () => {
		// The selection is a set the modal owns rather than something derived from the rendered rows.
		// Nothing asserted that end to end through a save, which is the one place the distinction
		// actually costs a machine its coverage.
		const onsave = vi.fn();
		render(MachineAssignmentModal, { props: baseProps({ onsave }) });

		await fireEvent.click(await screen.findByRole('checkbox', { name: /web-01/ }));
		await fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
		await fireEvent.click(await screen.findByRole('checkbox', { name: /db-01/ }));
		await fireEvent.click(screen.getByRole('button', { name: /save/i }));

		expect(onsave).toHaveBeenCalled();
		expect([...onsave.mock.calls[0][0]].sort()).toEqual([1, 2]);
	});
});
