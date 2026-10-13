// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';

// The real ApiClient is used here on purpose: it reads the body of a 403 and throws the server's
// message, or a bare "Forbidden" when there is none, and it reports a 404 from the status alone, so
// what the component shows is decided by what the client really hands it.
const { goto } = vi.hoisted(() => ({ goto: vi.fn() }));

vi.mock('$app/navigation', () => ({ goto }));

import RemoveMachineControl from './RemoveMachineControl.svelte';

const FORBIDDEN_WORDING =
	"You can't remove hosts right now. Your role or subscription doesn't allow changes; if your subscription is canceled, reactivate it from the billing page.";

function respondWithNoBody(status: number) {
	const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status }));
	vi.stubGlobal('fetch', fetchMock);

	return fetchMock;
}

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

	it('shows the reason the server gave when it refuses the removal with a 403 message', async () => {
		const message =
			'Subscription is canceled. Your account is in read-only mode. Please reactivate from the billing page.';
		const fetchMock = respondWith(403, message);

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent(message);
		expect(screen.queryByText(FORBIDDEN_WORDING)).not.toBeInTheDocument();
		expect(goto).not.toHaveBeenCalled();
		expect(fetchMock).toHaveBeenCalledTimes(1);
		const [url, init] = fetchMock.mock.calls[0];
		expect(url).toBe('/api/v1/machines/7');
		expect(init.method).toBe('DELETE');
		expect(init.headers['X-CSRF-TOKEN']).toBe('csrf');
	});

	it('explains the likely causes itself when the 403 carries no body, as a role refusal does', async () => {
		respondWithNoBody(403);

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent(FORBIDDEN_WORDING);
		expect(screen.queryByText('Forbidden')).not.toBeInTheDocument();
		expect(goto).not.toHaveBeenCalled();
	});

	it('explains the likely causes itself when the 403 body says only Forbidden', async () => {
		respondWith(403, 'Forbidden');

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent(FORBIDDEN_WORDING);
	});

	it('says the host is already gone when the server answers 404', async () => {
		respondWith(404, 'Machine not found');

		await confirmRemoval();

		expect(await screen.findByRole('alert')).toHaveTextContent('This host has already been removed.');
		expect(screen.getByRole('link', { name: 'Back to machines' })).toHaveAttribute('href', '/machines');
		expect(goto).not.toHaveBeenCalled();
	});
});
