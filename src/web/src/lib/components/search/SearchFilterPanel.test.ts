// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { MachineSearchParams } from '$lib/api/types';
import SearchFilterPanel from './SearchFilterPanel.svelte';
import { getTypeFilterOptions } from '$lib/utils/enums';

function renderPanel(filters: MachineSearchParams = {}) {
	const onapply = vi.fn();
	render(SearchFilterPanel, { props: { filters, onapply } });

	return onapply;
}

// A paused host is never in a health bucket, so asking for Offline and Paused together can only
// return nothing. The panel makes the two exclusive rather than let that combination be built.
describe('SearchFilterPanel paused filter', () => {
	it('asks for paused hosts when the Paused box is checked', async () => {
		const onapply = renderPanel();

		await fireEvent.click(screen.getByRole('checkbox', { name: 'Paused' }));
		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));

		expect(onapply).toHaveBeenCalledWith({ paused: 'true' });
	});

	it('sends no paused parameter when the box is left unchecked', async () => {
		const onapply = renderPanel();

		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));

		expect(onapply).toHaveBeenCalledWith({});
	});

	it('drops the health selections when Paused is checked, since the two can never match', async () => {
		const onapply = renderPanel({ healthStatus: 'offline,critical' });

		await fireEvent.click(screen.getByRole('checkbox', { name: 'Paused' }));

		expect(screen.getByRole('checkbox', { name: 'Offline' })).not.toBeChecked();
		expect(screen.getByRole('checkbox', { name: 'Critical' })).not.toBeChecked();

		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));

		expect(onapply).toHaveBeenCalledWith({ paused: 'true' });
	});

	it('unchecks Paused when a health status is chosen', async () => {
		const onapply = renderPanel({ paused: 'true' });

		await fireEvent.click(screen.getByRole('checkbox', { name: 'Offline' }));

		expect(screen.getByRole('checkbox', { name: 'Paused' })).not.toBeChecked();

		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));

		expect(onapply).toHaveBeenCalledWith({ healthStatus: 'offline' });
	});

	it('shows Paused checked when the page was opened with the filter', () => {
		renderPanel({ paused: 'true' });

		expect(screen.getByRole('checkbox', { name: 'Paused' })).toBeChecked();
	});

	it('clears the Paused filter with everything else', async () => {
		const onapply = renderPanel({ paused: 'true' });

		await fireEvent.click(screen.getByRole('button', { name: 'Clear All' }));

		expect(screen.getByRole('checkbox', { name: 'Paused' })).not.toBeChecked();
		expect(onapply).toHaveBeenCalledWith({});
	});
});

describe('SearchFilterPanel type filter', () => {
	// The select has no label of its own; "All Types" is the option it starts on.
	function typeSelect() {
		return screen.getByDisplayValue('All Types') as HTMLSelectElement;
	}

	it('offers the single-board computer', () => {
		renderPanel();

		const labels = Array.from(typeSelect().options).map((o) => o.textContent?.trim());
		expect(labels).toContain('Single-board computer');
	});

	it('asks for single-board computers by their enum name', async () => {
		const onapply = renderPanel();

		await fireEvent.change(typeSelect(), { target: { value: 'SingleBoardComputer' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Search' }));

		expect(onapply).toHaveBeenCalledWith({ type: 'SingleBoardComputer' });
	});

	it('shows the type the page was opened with', () => {
		renderPanel({ type: 'SingleBoardComputer' });

		expect(screen.getByDisplayValue('Single-board computer')).toBeInTheDocument();
	});

	it('offers every machine type the enum has, so a new one cannot be left out of the filter', () => {
		renderPanel();

		// Unknown is not a kind of machine anyone searches for, so the panel leaves it out.
		const expected = getTypeFilterOptions().filter((o) => o.value !== 'Unknown');
		const offered = Array.from(typeSelect().options).map((o) => ({
			value: o.value,
			label: o.textContent?.trim()
		}));

		expect(offered).toEqual(expected);
	});
});
