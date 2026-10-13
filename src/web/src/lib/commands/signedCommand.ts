// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { ApiClient } from '$lib/api/client';
import type { CommandDto, SigningKeyDto } from '$lib/api/types';
import { buildCanonicalPayload, generateNonce, signPayload } from '$lib/crypto/signing';

/** A signing key held in this browser, as getLocalKeys lists it. */
export interface LocalSigningKey {
	id: number;
	label: string;
	publicKeyBase64: string;
}

/** A key held in this browser that the server also knows, so it can sign a command the server will accept. */
export interface UsableSigningKey {
	localKeyId: number;
	serverKeyId: number;
	label: string;
}

export interface SignedCommandRequest {
	machineId: number;
	commandType: string;
	/** The local key to sign with, or null when the user has chosen none. */
	localKeyId: number | null;
	localKeys: LocalSigningKey[];
	/** The tenant's signing keys that have not been revoked. */
	serverKeys: SigningKeyDto[];
	userId: number;
	tenantId: number;
	/**
	 * The command's parameters, a string-to-string map at every hop. They are signed as given and sent
	 * as the request's Params; leave them out for a command that takes none.
	 */
	params?: Record<string, string>;
}

/** What the machine page collects for the commands that take parameters. */
export interface CommandParamFields {
	pid: string;
	force: boolean;
	sessionId: string;
}

// How long an agent accepts a command after it was signed.
const COMMAND_LIFETIME_MS = 10 * 60 * 1000;

// The agent refuses to kill a process at or below this pid, so the form refuses it too.
export const PROTECTED_PID_CEILING = 100;

const PID_PATTERN = /^[1-9][0-9]*$/;

// The agent's session id rule: a leading alphanumeric keeps the id from being read as an option.
const SESSION_ID_PATTERN = /^[a-zA-Z0-9][a-zA-Z0-9_-]*$/;

export function isValidPid(pid: string): boolean {
	return PID_PATTERN.test(pid) && Number(pid) > PROTECTED_PID_CEILING;
}

export function isValidSessionId(sessionId: string): boolean {
	return SESSION_ID_PATTERN.test(sessionId);
}

/**
 * The parameters to sign for a command from what the form holds: an object (empty for a command that
 * takes none), or null when the command's own fields are not valid yet. Only the selected command's
 * fields are read, and force is present only when it is ticked.
 */
export function buildCommandParams(commandType: string, fields: CommandParamFields): Record<string, string> | null {
	if (commandType === 'kill_process') {
		if (isValidPid(fields.pid) === false) {
			return null;
		}

		return fields.force ? { pid: fields.pid, force: 'true' } : { pid: fields.pid };
	}

	if (commandType === 'kill_session') {
		return isValidSessionId(fields.sessionId) ? { session_id: fields.sessionId } : null;
	}

	return {};
}

export function findServerKey(localKey: LocalSigningKey, serverKeys: SigningKeyDto[]): SigningKeyDto | undefined {
	return serverKeys.find((sk) => sk.publicKey === localKey.publicKeyBase64);
}

export function usableSigningKeys(localKeys: LocalSigningKey[], serverKeys: SigningKeyDto[]): UsableSigningKey[] {
	const usable: UsableSigningKey[] = [];
	for (const localKey of localKeys) {
		const serverKey = findServerKey(localKey, serverKeys);
		if (serverKey !== undefined) {
			usable.push({ localKeyId: localKey.id, serverKeyId: serverKey.id, label: localKey.label });
		}
	}

	return usable;
}

/**
 * Signs a command for one machine in the browser and submits it. Throws an Error whose message is fit
 * to show when the key cannot be used, and lets the API's error through when the server refuses it.
 *
 * The signed payload always carries a params object: the request's params, or an empty one for a command
 * that takes none, because the agent binds the command it runs to the bytes that were signed and reads
 * an absent params as empty. The request's own Params is the same map as a JSON string, and null when
 * the map is empty, because the server stores it in a jsonb column, which refuses an empty string.
 */
export async function sendSignedCommand(
	api: Pick<ApiClient, 'sendCommand'>,
	request: SignedCommandRequest
): Promise<CommandDto> {
	if (request.localKeyId === null) {
		throw new Error('No signing key selected. Generate one in Settings > Signing Keys.');
	}

	const localKey = request.localKeys.find((k) => k.id === request.localKeyId);
	if (localKey === undefined) {
		throw new Error('Selected key not found.');
	}

	const serverKey = findServerKey(localKey, request.serverKeys);
	if (serverKey === undefined) {
		throw new Error('This key is not registered on the server. Re-register it in Settings > Signing Keys.');
	}

	// One copy feeds both the signed bytes and the request, so the two cannot decode to different maps.
	const params: Record<string, string> = { ...request.params };

	const commandId = crypto.randomUUID();
	const nonce = generateNonce();
	const now = new Date();
	const timestamp = now.toISOString();
	const expiresAt = new Date(now.getTime() + COMMAND_LIFETIME_MS).toISOString();

	const canonicalPayload = buildCanonicalPayload({
		command_id: commandId,
		command_type: request.commandType,
		expires_at: expiresAt,
		machine_id: request.machineId,
		nonce,
		params,
		tenant_id: request.tenantId,
		timestamp,
		user_id: request.userId
	});

	const signature = await signPayload(localKey.id, canonicalPayload);

	return api.sendCommand({
		commandId,
		machineId: request.machineId,
		signingKeyId: serverKey.id,
		commandType: request.commandType,
		params: Object.keys(params).length > 0 ? JSON.stringify(params) : null,
		nonce,
		signature,
		canonicalPayload,
		timestamp,
		expiresAt
	});
}
