// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

const DB_NAME = 'vord-signing-keys';
const DB_VERSION = 1;
const STORE_NAME = 'keys';

interface StoredKeyPair {
	userId: number;
	tenantId: number;
	label: string;
	publicKeyBase64: string;
	privateKey: Uint8Array;
	createdAt: string;
}

function openDb(): Promise<IDBDatabase> {
	return new Promise((resolve, reject) => {
		const request = indexedDB.open(DB_NAME, DB_VERSION);
		request.onupgradeneeded = () => {
			const db = request.result;
			if (db.objectStoreNames.contains(STORE_NAME) === false) {
				const store = db.createObjectStore(STORE_NAME, { keyPath: 'id', autoIncrement: true });
				store.createIndex('user_tenant', ['userId', 'tenantId'], { unique: false });
			}
		};
		request.onsuccess = () => resolve(request.result);
		request.onerror = () => reject(request.error);
	});
}

function txStore(db: IDBDatabase, mode: IDBTransactionMode): IDBObjectStore {
	return db.transaction(STORE_NAME, mode).objectStore(STORE_NAME);
}

/**
 * Generates an Ed25519 key pair via WebCrypto, stores the private key in IndexedDB,
 * and returns the base64-encoded public key for registration with the server.
 */
export async function generateKeyPair(
	userId: number,
	tenantId: number,
	label: string
): Promise<{ publicKeyBase64: string }> {
	const keyPair = await crypto.subtle.generateKey('Ed25519', true, ['sign', 'verify']);

	const publicKeyRaw = await crypto.subtle.exportKey('raw', keyPair.publicKey);
	const privateKeyPkcs8 = await crypto.subtle.exportKey('pkcs8', keyPair.privateKey);

	const publicKeyBase64 = btoa(String.fromCharCode(...new Uint8Array(publicKeyRaw)));

	const stored: StoredKeyPair = {
		userId,
		tenantId,
		label,
		publicKeyBase64,
		privateKey: new Uint8Array(privateKeyPkcs8),
		createdAt: new Date().toISOString()
	};

	const db = await openDb();
	await new Promise<void>((resolve, reject) => {
		const req = txStore(db, 'readwrite').add(stored);
		req.onsuccess = () => resolve();
		req.onerror = () => reject(req.error);
	});
	db.close();

	return { publicKeyBase64 };
}

/**
 * Gets all locally stored key pairs for a user/tenant.
 */
export async function getLocalKeys(
	userId: number,
	tenantId: number
): Promise<Array<{ id: number; label: string; publicKeyBase64: string; createdAt: string }>> {
	const db = await openDb();
	const store = txStore(db, 'readonly');
	const index = store.index('user_tenant');

	return new Promise((resolve, reject) => {
		const req = index.getAll([userId, tenantId]);
		req.onsuccess = () => {
			const results = (req.result as (StoredKeyPair & { id: number })[]).map((r) => ({
				id: r.id,
				label: r.label,
				publicKeyBase64: r.publicKeyBase64,
				createdAt: r.createdAt
			}));
			db.close();
			resolve(results);
		};
		req.onerror = () => {
			db.close();
			reject(req.error);
		};
	});
}

/**
 * Deletes a local key pair from IndexedDB (e.g., after server-side revocation).
 */
export async function deleteLocalKey(id: number): Promise<void> {
	const db = await openDb();
	await new Promise<void>((resolve, reject) => {
		const req = txStore(db, 'readwrite').delete(id);
		req.onsuccess = () => resolve();
		req.onerror = () => reject(req.error);
	});
	db.close();
}

/**
 * Builds the canonical JSON payload for signing.
 * Keys are alphabetically sorted, no whitespace.
 */
export function buildCanonicalPayload(fields: {
	command_id: string;
	command_type: string;
	expires_at: string;
	machine_id: number;
	nonce: string;
	params: Record<string, string> | null;
	tenant_id: number;
	timestamp: string;
	user_id: number;
}): string {
	const sorted: Record<string, unknown> = {};
	for (const key of Object.keys(fields).sort()) {
		const value = (fields as Record<string, unknown>)[key];
		if (value !== null && value !== undefined) {
			sorted[key] = value;
		}
	}

	return JSON.stringify(sorted);
}

/**
 * Signs the canonical payload using the private key stored in IndexedDB.
 */
export async function signPayload(
	localKeyId: number,
	canonicalPayload: string
): Promise<string> {
	const db = await openDb();
	const stored = await new Promise<StoredKeyPair & { id: number }>((resolve, reject) => {
		const req = txStore(db, 'readonly').get(localKeyId);
		req.onsuccess = () => {
			if (req.result === undefined) {
				reject(new Error('Key not found in local storage'));
			} else {
				resolve(req.result);
			}
		};
		req.onerror = () => reject(req.error);
	});
	db.close();

	const privateKey = await crypto.subtle.importKey(
		'pkcs8',
		stored.privateKey.buffer as ArrayBuffer,
		'Ed25519',
		false,
		['sign']
	);

	const data = new TextEncoder().encode(canonicalPayload);
	const signature = await crypto.subtle.sign('Ed25519', privateKey, data);

	return btoa(String.fromCharCode(...new Uint8Array(signature)));
}

/**
 * Generates a 16-byte random hex nonce for replay prevention.
 */
export function generateNonce(): string {
	const bytes = new Uint8Array(16);
	crypto.getRandomValues(bytes);

	return Array.from(bytes)
		.map((b) => b.toString(16).padStart(2, '0'))
		.join('');
}

/**
 * Auto-generates a label from the browser's user-agent.
 */
export function autoLabel(): string {
	const ua = navigator.userAgent;
	if (ua.includes('Mac')) return 'macOS Browser';
	if (ua.includes('Windows')) return 'Windows Browser';
	if (ua.includes('Linux')) return 'Linux Browser';

	return 'Browser Key';
}
