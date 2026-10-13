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

import { buildCommandParams, findServerKey, isValidPid, isValidSessionId, sendSignedCommand, usableSigningKeys } from './signedCommand';
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

	describe('command parameters', () => {
		it('signs exactly the params it was given, so the signed bytes bind the command to them', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ commandType: 'kill_process', params: { pid: '4242', force: 'true' } }));

			const payload = JSON.parse(sent[0].canonicalPayload) as { params: unknown };

			expect(payload.params).toEqual({ pid: '4242', force: 'true' });
		});

		it('sends the same string map in the request as it signed', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ commandType: 'kill_session', params: { session_id: 'c12' } }));

			const payload = JSON.parse(sent[0].canonicalPayload) as { params: unknown };

			expect(typeof sent[0].params).toBe('string');
			expect(JSON.parse(sent[0].params as string)).toEqual({ session_id: 'c12' });
			expect(JSON.parse(sent[0].params as string)).toEqual(payload.params);
		});

		it('keeps the canonical payload sorted at the top level with the params in place', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ commandType: 'kill_process', params: { pid: '4242' } }));

			expect(sent[0].canonicalPayload).toBe(
				`{"command_id":"${sent[0].commandId}","command_type":"kill_process","expires_at":"${EXPIRES}",` +
					`"machine_id":7,"nonce":"${sent[0].nonce}","params":{"pid":"4242"},"tenant_id":2,"timestamp":"${NOW}","user_id":5}`
			);
		});

		it('signs an empty object and sends null when the params are empty', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ params: {} }));

			expect(sent[0].canonicalPayload).toContain('"params":{}');
			expect(sent[0].params).toBeNull();
		});

		it('signs an empty object and sends null when no params are given, as the reporter commands do', async () => {
			const { api, sent } = makeApi();

			await sendSignedCommand(api, request({ commandType: 'k8s_reporter_uninstall' }));

			expect(sent[0].canonicalPayload).toContain('"params":{}');
			expect(sent[0].params).toBeNull();
		});

		it('is not changed by the caller mutating its params after the call starts', async () => {
			const { api, sent } = makeApi();
			const params: Record<string, string> = { pid: '4242' };

			const pending = sendSignedCommand(api, request({ commandType: 'kill_process', params }));
			params.pid = '1';
			await pending;

			const payload = JSON.parse(sent[0].canonicalPayload) as { params: unknown };

			expect(payload.params).toEqual({ pid: '4242' });
			expect(JSON.parse(sent[0].params as string)).toEqual({ pid: '4242' });
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

	describe('isValidPid', () => {
		it.each(['101', '102', '4242', '4194304'])('accepts %s', (pid) => {
			expect(isValidPid(pid)).toBe(true);
		});

		it.each(['', '0', '1', '99', '100', 'abc', '-5', '12a', ' 4242', '4242 ', '1.5', '0101', '+101', '1e3'])(
			'refuses %j',
			(pid) => {
				expect(isValidPid(pid)).toBe(false);
			}
		);
	});

	describe('isValidSessionId', () => {
		it.each(['1', 'c12', 'session-42', 'sess_01', 'A', 'a-b_c'])('accepts %s', (id) => {
			expect(isValidSessionId(id)).toBe(true);
		});

		it.each(['', '-h', '--help', '-1', '_abc', 'ses id', 'ses;rm', 'ses$(id)', '../x', 'a\nb', 'c1\n', ' c1', 'c1 '])(
			'refuses %j',
			(id) => {
				expect(isValidSessionId(id)).toBe(false);
			}
		);
	});

	describe('buildCommandParams', () => {
		const blank = { pid: '', force: false, sessionId: '' };

		it('gives Kill Process its pid and leaves force out when it is not ticked', () => {
			const params = buildCommandParams('kill_process', { ...blank, pid: '4242' });

			expect(params).toEqual({ pid: '4242' });
			expect(params).not.toHaveProperty('force');
		});

		it('adds force="true" only when the box is ticked', () => {
			expect(buildCommandParams('kill_process', { ...blank, pid: '4242', force: true })).toEqual({
				pid: '4242',
				force: 'true'
			});
		});

		it.each(['', '0', '100', 'abc', '-5'])('is null for the invalid Kill Process pid %j', (pid) => {
			expect(buildCommandParams('kill_process', { ...blank, pid })).toBeNull();
		});

		it('gives Kill Session its session_id', () => {
			expect(buildCommandParams('kill_session', { ...blank, sessionId: 'c12' })).toEqual({ session_id: 'c12' });
		});

		it.each(['', '-h', 'a b'])('is null for the invalid Kill Session id %j', (sessionId) => {
			expect(buildCommandParams('kill_session', { ...blank, sessionId })).toBeNull();
		});

		it('ignores the other commands\' fields', () => {
			expect(buildCommandParams('kill_session', { pid: '4242', force: true, sessionId: 'c12' })).toEqual({
				session_id: 'c12'
			});
			expect(buildCommandParams('kill_process', { pid: '4242', force: false, sessionId: 'bad id' })).toEqual({
				pid: '4242'
			});
		});

		it.each(['reboot', 'check_updates', 'install_updates'])('is an empty object for %s, whatever the fields hold', (type) => {
			expect(buildCommandParams(type, { pid: 'junk', force: true, sessionId: '-x' })).toEqual({});
		});
	});
});
