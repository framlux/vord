// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { CommandResult } from '$lib/utils/clusterReporting';
import CommandResults from './CommandResults.svelte';

function result(overrides: Partial<CommandResult> = {}): CommandResult {
	return {
		machineId: 7,
		machineName: 'cp-1',
		commandType: 'k8s_reporter_enrol',
		accepted: true,
		message: 'Accepted',
		...overrides
	};
}

describe('CommandResults', () => {
	it('renders nothing for an empty list', () => {
		const { container } = render(CommandResults, { props: { results: [] } });

		expect(container.textContent?.trim()).toBe('');
	});

	it('lists each machine with the command it was sent and the outcome', () => {
		render(CommandResults, {
			props: {
				results: [
					result({ machineId: 1, machineName: 'cp-1', commandType: 'k8s_reporter_uninstall' }),
					result({ machineId: 2, machineName: 'cp-2', commandType: 'k8s_reporter_unenrol', message: 'Accepted' })
				]
			}
		});

		const items = within(screen.getByTestId('command-results')).getAllByRole('listitem');

		expect(items).toHaveLength(2);
		expect(items[0]).toHaveTextContent('cp-1');
		expect(items[0]).toHaveTextContent('Uninstall');
		expect(items[1]).toHaveTextContent('cp-2');
		expect(items[1]).toHaveTextContent('Unenrol');
	});

	it('links a machine to its page, where the command history shows the outcome', () => {
		render(CommandResults, { props: { results: [result({ machineId: 7, machineName: 'cp-1' })] } });

		expect(screen.getByRole('link', { name: 'cp-1' })).toHaveAttribute('href', '/machines/7');
	});

	it('marks an accepted command and a refused one differently, with the API message of the refusal', () => {
		render(CommandResults, {
			props: {
				results: [
					result({ machineId: 1, machineName: 'cp-1', accepted: true, message: 'Accepted' }),
					result({
						machineId: 2,
						machineName: 'cp-2',
						accepted: false,
						message: 'Only control-plane machines with an admin credential can enrol'
					})
				]
			}
		});

		const items = within(screen.getByTestId('command-results')).getAllByRole('listitem');

		expect(items[0]).toHaveAttribute('data-outcome', 'accepted');
		expect(items[0]).toHaveTextContent('Accepted');
		expect(items[1]).toHaveAttribute('data-outcome', 'failed');
		expect(items[1]).toHaveTextContent('Only control-plane machines with an admin credential can enrol');
	});
});
