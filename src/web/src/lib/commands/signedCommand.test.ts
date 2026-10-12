// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import type { CommandDto, SendCommandRequest, SigningKeyDto } from '$lib/api/types';

const { signPayload } = vi.hoisted(() => ({ signPayload: vi.fn() }));

vi.mock('$lib/crypto/signing', async (importOriginal) => ({
	...(await importOriginal<typeof import('$lib/crypto/signing')>()),
	signPayload
}));

import { findServerKey, sendSignedCommand, usableSigningKeys } from './signedCommand';
import type { LocalSigningKey } from './signedCommand';

const NOW = '2026-10-12T09:30:00.123Z';
const EXPIRES = '2026-10-12T09:40:00.123Z';

function localKey(overrides: Partial<LocalSigningKey> = {}): LocalSigningKey {
	return { id: 3, label: 'Laptop', publicKeyBase64: 'PUBLIC-A', ...overrides };
}

function serverKey(overrides: Partial<SigningKeyDto> = {}): SigningKeyDto {
	return {
		id: 41,
		label: 'Laptop',
		publicKey: 'PUBLIC-A',
		fingerprint: 'fp-a',
		createdAt: '2026-10-01T00:00:00Z',
		revokedAt: null,
		...overrides
	};
}

const command: CommandDto = {
	id: 9,
	commandId: 'c',
	machineId: 7,
	commandType: 'k8s_reporter_enrol',
	status: 'Pending',
	createdAt: NOW,
	expiresAt: EXPIRES,
	deliveredAt: null,
	completedAt: null,
	exitCode: null,
	resultMessage: null
};

function makeApi() {
	const sent: SendCommandRequest[] = [];
	const sendCommand = vi.fn(async (req: SendCommandRequest) => {
		sent.push(req);

		return command;
	});

	return { api: { sendCommand }, sent };
}

function request(overrides: Partial<Parameters<typeof sendSignedCommand>[1]> = {}) {
	return {
		machineId: 7,
		commandType: 'k8s_reporter_enrol',
		localKeyId: 3,
		localKeys: [localKey()],
		serverKeys: [serverKey()],
		userId: 5,
		tenantId: 2,
		...overrides
	};
}

describe('signedCommand', () => {
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ['Date'] });
		vi.setSystemTime(new Date(NOW));
		signPayload.mockReset();
		signPayload.mockResolvedValue('SIGNATURE');
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	describe('the canonical payload', () => {
		it('carries an empty params object, so the signed bytes always bind the command to no parameters', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request());

			expect(sent[0].canonicalPayload).toContain('"params":{}');
		});

		it('is the sorted, whitespace-free JSON the agent verifies', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request());

			const parsed = JSON.parse(sent[0].canonicalPayload) as Record<string, unknown>;

			expect(sent[0].canonicalPayload).toBe(
				`{"command_id":"${sent[0].commandId}","command_type":"k8s_reporter_enrol","expires_at":"${EXPIRES}",` +
					`"machine_id":7,"nonce":"${sent[0].nonce}","params":{},"tenant_id":2,"timestamp":"${NOW}","user_id":5}`
			);
			expect(Object.keys(parsed)).toEqual([...Object.keys(parsed)].sort());
		});

		it('expires ten minutes after it was issued', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request());

			expect(sent[0].timestamp).toBe(NOW);
			expect(sent[0].expiresAt).toBe(EXPIRES);
		});

		it('is what gets signed, with the local key the caller named', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ localKeyId: 3 }));

			expect(signPayload).toHaveBeenCalledTimes(1);
			expect(signPayload).toHaveBeenCalledWith(3, sent[0].canonicalPayload);
		});
	});

	describe('the request', () => {
		it('sends Params as null, never as an empty string, because the server stores it in a jsonb column', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request());

			expect(sent[0].params).toBeNull();
			expect(sent[0].params).not.toBe('');
		});

		it('names the machine, the command and the server-side key', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ machineId: 12, commandType: 'k8s_reporter_uninstall' }));

			expect(sent[0]).toMatchObject({
				machineId: 12,
				commandType: 'k8s_reporter_uninstall',
				signingKeyId: 41,
				signature: 'SIGNATURE'
			});
		});

		it('gives every command its own id and nonce', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request());
			await sendSignedCommand(api, request());

			expect(sent[0].commandId).not.toBe(sent[1].commandId);
			expect(sent[0].nonce).not.toBe(sent[1].nonce);
			expect(sent[0].nonce).toMatch(/^[0-9a-f]{32}$/);
		});

		it('returns what the API answered', async () => {
			const { api } = makeApi();

			await expect(sendSignedCommand(api, request())).resolves.toBe(command);
		});

		it('lets the API error through, so the caller can show its message', async () => {
			const api = { sendCommand: vi.fn().mockRejectedValue(new Error('This host is paused')) };

			await expect(sendSignedCommand(api, request())).rejects.toThrow('This host is paused');
		});
	});

	describe('when the key cannot be used', () => {
		it('refuses when no key was selected, without signing or sending', async () => {
			const { api } = makeApi();

			await expect(sendSignedCommand(api, request({ localKeyId: null }))).rejects.toThrow(
				'No signing key selected. Generate one in Settings > Signing Keys.'
			);

			expect(signPayload).not.toHaveBeenCalled();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('refuses when the selected key is not among the local keys', async () => {
			const { api } = makeApi();

			await expect(sendSignedCommand(api, request({ localKeyId: 99 }))).rejects.toThrow('Selected key not found.');

			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('refuses when the server does not know the local key', async () => {
			const { api } = makeApi();

			await expect(
				sendSignedCommand(api, request({ serverKeys: [serverKey({ publicKey: 'PUBLIC-OTHER' })] }))
			).rejects.toThrow('This key is not registered on the server. Re-register it in Settings > Signing Keys.');

			expect(signPayload).not.toHaveBeenCalled();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});
	});

	describe('findServerKey', () => {
		it('matches a local key to the server key with the same public key', () => {
			const keys = [serverKey({ id: 1, publicKey: 'X' }), serverKey({ id: 2, publicKey: 'PUBLIC-A' })];

			expect(findServerKey(localKey(), keys)?.id).toBe(2);
		});

		it('finds nothing when no server key has that public key', () => {
			expect(findServerKey(localKey(), [serverKey({ publicKey: 'X' })])).toBeUndefined();
		});
	});

	describe('usableSigningKeys', () => {
		it('lists the local keys the server knows, with both ids', () => {
			const usable = usableSigningKeys(
				[localKey({ id: 3, label: 'Laptop' }), localKey({ id: 4, label: 'Old', publicKeyBase64: 'GONE' })],
				[serverKey({ id: 41 })]
			);

			expect(usable).toEqual([{ localKeyId: 3, serverKeyId: 41, label: 'Laptop' }]);
		});

		it('is empty when the device holds no keys', () => {
			expect(usableSigningKeys([], [serverKey()])).toEqual([]);
		});
	});
});
