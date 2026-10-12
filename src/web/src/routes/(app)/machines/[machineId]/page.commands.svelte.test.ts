// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineType, OperatingSystem } from '$lib/api/types';
import type {
	CommandDto,
	MachineAuthorizedKeyDto,
	MachineDto,
	SigningKeyDto,
	SubscriptionDto,
	UserDto
} from '$lib/api/types';

const { api, signPayload, getLocalKeys } = vi.hoisted(() => ({
	api: {
		sendCommand: vi.fn(),
		getSigningKeys: vi.fn(),
		getCommandHistory: vi.fn(),
		getMachineAuthorizedKeys: vi.fn(),
		getMachineStatus: vi.fn()
	},
	signPayload: vi.fn(),
	getLocalKeys: vi.fn()
}));

vi.mock('$app/navigation', () => ({ invalidateAll: vi.fn(), goto: vi.fn() }));
vi.mock('$app/forms', () => ({ enhance: () => ({}) }));

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
			sendCommand = api.sendCommand;
			getSigningKeys = api.getSigningKeys;
			getCommandHistory = api.getCommandHistory;
			getMachineAuthorizedKeys = api.getMachineAuthorizedKeys;
			getMachineStatus = api.getMachineStatus;
		}
	};
});

vi.mock('$lib/crypto/signing', async (importOriginal) => ({
	...(await importOriginal<typeof import('$lib/crypto/signing')>()),
	signPayload,
	getLocalKeys
}));

import MachinePage from './+page.svelte';

const machine: MachineDto = {
	id: 7,
	name: 'web-01',
	description: null,
	location: null,
	hostname: 'web-01.acme.co',
	operatingSystem: OperatingSystem.Ubuntu,
	machineType: MachineType.VirtualMachine,
	serialNumber: 'SN-7',
	assetTag: null,
	isOnline: true,
	lastPing: '2026-10-12T09:00:00Z',
	registeredOn: '2026-01-01T00:00:00Z',
	isDeleted: false,
	commandsEnabled: true,
	isPaused: false
};

const subscription: SubscriptionDto = {
	tier: 'Team',
	status: 'Active',
	machineLimit: 1000,
	machineCount: 3,
	retentionDays: 60,
	currentPeriodEnd: null,
	cancelAtPeriodEnd: false,
	billingInterval: 'monthly',
	pendingAction: null,
	alertRuleLimit: 10,
	alertRuleCount: 0,
	webhookLimit: 3,
	webhookCount: 0,
	memberLimit: 5,
	memberCount: 1,
	pausedMachineCount: 0,
	billableMachineCount: null,
	machineLimitIsCustom: false
};

const user: UserDto = {
	id: 5,
	name: 'Jonathan Miller',
	email: 'jonathan@acme.co',
	avatar: '',
	isGlobalAdmin: false,
	uniqueId: 'uid-1',
	needsOnboarding: false,
	tenants: [{ tenantId: 2, tenantName: 'Acme Corp', role: '1' }],
	activeTenantId: 2,
	deployment: { selfHosted: false },
	csrfToken: 'csrf'
};

const serverKey: SigningKeyDto = {
	id: 41,
	label: 'Laptop',
	publicKey: 'PUBLIC-A',
	fingerprint: 'fp-a',
	createdAt: '2026-10-01T00:00:00Z',
	revokedAt: null
};

const authorization: MachineAuthorizedKeyDto = {
	id: 1,
	signingKeyId: 41,
	label: 'Laptop',
	fingerprint: 'fp-a',
	ownerUsername: 'jonathan',
	authorizedAt: '2026-10-02T00:00:00Z',
	authorizedByUsername: 'jonathan',
	revokedAt: null,
	isActive: true
};

const accepted: CommandDto = {
	id: 1,
	commandId: 'c',
	machineId: 7,
	commandType: 'reboot',
	status: 'Pending',
	createdAt: '2026-10-12T09:30:00Z',
	expiresAt: '2026-10-12T09:40:00Z',
	deliveredAt: null,
	completedAt: null,
	exitCode: null,
	resultMessage: null
};

async function openCommandsTab() {
	render(MachinePage, {
		props: {
			data: {
				machine,
				machineDetail: null,
				authorizedKeys: [authorization],
				machineAlertRules: [],
				allAlertRules: [],
				subscription,
				user
			}
		}
	});

	await fireEvent.click(screen.getByRole('tab', { name: 'Commands' }));
	await screen.findByRole('button', { name: 'Send' });
}

describe('machine page commands', () => {
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ['Date'] });
		vi.setSystemTime(new Date('2026-10-12T09:30:00.123Z'));
		for (const fn of Object.values(api)) {
			fn.mockReset();
		}
		api.getSigningKeys.mockResolvedValue({ keys: [serverKey], activeCount: 1, maxKeys: 5 });
		api.getCommandHistory.mockResolvedValue([]);
		api.getMachineAuthorizedKeys.mockResolvedValue([authorization]);
		api.sendCommand.mockResolvedValue(accepted);
		signPayload.mockReset();
		signPayload.mockResolvedValue('SIGNATURE');
		getLocalKeys.mockReset();
		getLocalKeys.mockResolvedValue([
			{ id: 3, label: 'Laptop', publicKeyBase64: 'PUBLIC-A', createdAt: '2026-10-01T00:00:00Z' }
		]);
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('signs and sends the chosen command to this machine', async () => {
		await openCommandsTab();

		await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

		await waitFor(() => expect(api.sendCommand).toHaveBeenCalledTimes(1));
		expect(api.sendCommand.mock.calls[0][0]).toMatchObject({
			machineId: 7,
			commandType: 'reboot',
			signingKeyId: 41,
			signature: 'SIGNATURE',
			params: null
		});
		expect(signPayload).toHaveBeenCalledWith(3, api.sendCommand.mock.calls[0][0].canonicalPayload);
		expect(await screen.findByText('reboot command sent successfully.')).toBeInTheDocument();
	});

	it('signs a canonical payload for this tenant and user that carries an empty params object', async () => {
		await openCommandsTab();

		await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

		await waitFor(() => expect(api.sendCommand).toHaveBeenCalledTimes(1));
		const payload = JSON.parse(api.sendCommand.mock.calls[0][0].canonicalPayload) as Record<string, unknown>;

		expect(payload).toMatchObject({
			command_type: 'reboot',
			machine_id: 7,
			tenant_id: 2,
			user_id: 5,
			params: {}
		});
	});

	it('shows the API message when the server refuses the command', async () => {
		api.sendCommand.mockRejectedValue(new Error('Remote commands are not enabled on this machine'));
		await openCommandsTab();

		await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

		expect(await screen.findByText('Remote commands are not enabled on this machine')).toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
	});
});
