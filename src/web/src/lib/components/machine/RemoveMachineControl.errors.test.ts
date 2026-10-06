// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';

// The real ApiClient is used here on purpose: it reports a 403 and a 404 from the status alone and
// never reads the body, so the messages shown to the user cannot come from the server's envelope.
const { goto } = vi.hoisted(() => ({ goto: vi.fn() }));

vi.mock('$app/navigation', () => ({ goto }));

import RemoveMachineControl from './RemoveMachineControl.svelte';

function respondWith(status: number, message: string) {
	const fetchMock = vi.fn().mockResolvedValue(
		new Response(JSON.stringify({ success: false, message, data: null }), {
			status,
			headers: { 'Content-Type': 'application/json' }
		})
	);
	vi.stubGlobal('fetch', fetchMock);

	return fetchMock;
}

async function confirmRemoval() {
	render(RemoveMachineControl, { props: { machineId: 7, machineName: 'web-01', csrfToken: 'csrf' } });
	await fireEvent.click(screen.getByRole('button', { name: 'Remove host' }));
	await fireEvent.click(screen.getByRole('button', { name: 'Yes, remove' }));
}

describe('RemoveMachineControl against the real API client', () => {
	beforeEach(() => {
		goto.mockReset();
	});

	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it('explains why a host cannot be removed when the server refuses with a 403', async () => {
		const fetchMock = respondWith(403, 'Subscription is canceled; changes are read-only.');

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent(
			"You can't remove hosts right now. Your role or subscription doesn't allow changes; if your subscription is canceled, reactivate it from the billing page."
		);
		expect(screen.queryByText('Forbidden')).not.toBeInTheDocument();
		expect(goto).not.toHaveBeenCalled();
		expect(fetchMock).toHaveBeenCalledTimes(1);
		const [url, init] = fetchMock.mock.calls[0];
		expect(url).toBe('/api/v1/machines/7');
		expect(init.method).toBe('DELETE');
		expect(init.headers['X-CSRF-TOKEN']).toBe('csrf');
	});

	it('says the host is already gone when the server answers 404', async () => {
		respondWith(404, 'Machine not found');

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent('This host has already been removed.');
		expect(screen.getByRole('link', { name: 'Back to machines' })).toHaveAttribute('href', '/machines');
		expect(goto).not.toHaveBeenCalled();
	});
});
