// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/svelte';
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
		getMachineStatus: vi.fn(),
		authorizeMachineKey: vi.fn()
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
			authorizeMachineKey = api.authorizeMachineKey;
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

function renderPage(tier: string = subscription.tier) {
	return render(MachinePage, {
		props: {
			data: {
				machine,
				machineDetail: null,
				authorizedKeys: [authorization],
				machineAlertRules: [],
				allAlertRules: [],
				subscription: { ...subscription, tier },
				user
			}
		}
	});
}

async function openCommandsTab() {
	renderPage();

	await fireEvent.click(screen.getByRole('tab', { name: 'Commands' }));
	await screen.findByRole('button', { name: 'Send' });
}

function arrangeMocks() {
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
}

describe('machine page commands', () => {
	beforeEach(arrangeMocks);

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

async function chooseCommand(type: string) {
	await fireEvent.change(screen.getByLabelText('Command'), { target: { value: type } });
}

async function sentParams() {
	await waitFor(() => expect(api.sendCommand).toHaveBeenCalledTimes(1));
	const request = api.sendCommand.mock.calls[0][0];
	const signed = (JSON.parse(request.canonicalPayload) as { params: unknown }).params;

	return { signed, sent: request.params === null ? null : (JSON.parse(request.params) as unknown) };
}

describe('machine page command parameters', () => {
	beforeEach(arrangeMocks);

	afterEach(() => {
		vi.useRealTimers();
	});

	it.each(['reboot', 'check_updates', 'install_updates'])('shows no parameter fields for %s', async (type) => {
		await openCommandsTab();

		await chooseCommand(type);

		expect(screen.queryByLabelText('PID')).not.toBeInTheDocument();
		expect(screen.queryByLabelText('Force (SIGKILL immediately)')).not.toBeInTheDocument();
		expect(screen.queryByLabelText('Session ID')).not.toBeInTheDocument();
		expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
	});

	it('signs an empty params object and sends null for a command without parameters', async () => {
		await openCommandsTab();

		await chooseCommand('check_updates');
		await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

		expect(await sentParams()).toEqual({ signed: {}, sent: null });
	});

	describe('Kill Process', () => {
		it('shows a PID field and a force checkbox, and keeps Send disabled until a PID is entered', async () => {
			await openCommandsTab();

			await chooseCommand('kill_process');

			expect(screen.getByLabelText('PID')).toBeInTheDocument();
			expect(screen.getByLabelText('Force (SIGKILL immediately)')).not.toBeChecked();
			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
		});

		it.each(['0', '100', 'abc', '-5', '4 2', '1e3'])('keeps Send disabled for the PID %j', async (pid) => {
			await openCommandsTab();
			await chooseCommand('kill_process');

			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: pid } });

			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
			expect(screen.getByLabelText('PID')).toBeInvalid();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('says why a PID is refused, and does not scold before anything is typed', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');

			expect(screen.getByLabelText('PID')).not.toBeInvalid();
			expect(screen.getByLabelText('PID')).not.toHaveAccessibleDescription(/greater than 100/);

			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: '100' } });

			expect(screen.getByLabelText('PID')).toHaveAccessibleDescription(/greater than 100/);
		});

		it('signs and sends the pid alone when force is not ticked', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');

			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: '4242' } });
			expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
			await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

			const { signed, sent } = await sentParams();

			expect(api.sendCommand.mock.calls[0][0]).toMatchObject({ machineId: 7, commandType: 'kill_process' });
			expect(signed).toEqual({ pid: '4242' });
			expect(sent).toEqual({ pid: '4242' });
			expect(signed).not.toHaveProperty('force');
		});

		it('adds force="true" to both the signed payload and the request when the box is ticked', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');

			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: '4242' } });
			await fireEvent.click(screen.getByLabelText('Force (SIGKILL immediately)'));
			await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

			expect(await sentParams()).toEqual({
				signed: { pid: '4242', force: 'true' },
				sent: { pid: '4242', force: 'true' }
			});
		});

		it('leaves force out again once the box is unticked', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');

			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: '4242' } });
			await fireEvent.click(screen.getByLabelText('Force (SIGKILL immediately)'));
			await fireEvent.click(screen.getByLabelText('Force (SIGKILL immediately)'));
			await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

			expect(await sentParams()).toEqual({ signed: { pid: '4242' }, sent: { pid: '4242' } });
		});
	});

	describe('Kill Session', () => {
		it('shows a Session ID field with its helper text, and keeps Send disabled until one is entered', async () => {
			await openCommandsTab();

			await chooseCommand('kill_session');

			const field = screen.getByLabelText('Session ID');

			expect(field).toBeInTheDocument();
			expect(field).toHaveAccessibleDescription('The loginctl session id; run loginctl list-sessions on the host');
			expect(screen.queryByLabelText('PID')).not.toBeInTheDocument();
			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
		});

		it.each(['-h', '--help', '_x', 'a b', 'a;b', 'a/b'])('keeps Send disabled for the session id %j', async (id) => {
			await openCommandsTab();
			await chooseCommand('kill_session');

			await fireEvent.input(screen.getByLabelText('Session ID'), { target: { value: id } });

			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
			expect(screen.getByLabelText('Session ID')).toBeInvalid();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('signs and sends the session id', async () => {
			await openCommandsTab();
			await chooseCommand('kill_session');

			await fireEvent.input(screen.getByLabelText('Session ID'), { target: { value: 'c12' } });
			await fireEvent.click(screen.getByRole('button', { name: 'Send' }));

			expect(api.sendCommand.mock.calls[0][0]).toMatchObject({ machineId: 7, commandType: 'kill_session' });
			expect(await sentParams()).toEqual({ signed: { session_id: 'c12' }, sent: { session_id: 'c12' } });
		});
	});

	describe('changing the command', () => {
		it('clears the fields, so a pid typed for Kill Process is not carried to Kill Session or back', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');
			await fireEvent.input(screen.getByLabelText('PID'), { target: { value: '4242' } });
			await fireEvent.click(screen.getByLabelText('Force (SIGKILL immediately)'));

			await chooseCommand('kill_session');
			await fireEvent.input(screen.getByLabelText('Session ID'), { target: { value: 'c12' } });
			await chooseCommand('kill_process');

			expect(screen.getByLabelText('PID')).toHaveValue('');
			expect(screen.getByLabelText('Force (SIGKILL immediately)')).not.toBeChecked();
			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();

			await chooseCommand('kill_session');

			expect(screen.getByLabelText('Session ID')).toHaveValue('');
			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
		});

		it('enables Send again for a command without parameters', async () => {
			await openCommandsTab();
			await chooseCommand('kill_process');
			expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();

			await chooseCommand('reboot');

			expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
		});
	});
});

describe('machine page tabs by tier', () => {
	const teamNotice = 'Remote commands require a Team subscription.';

	beforeEach(arrangeMocks);

	afterEach(() => {
		vi.useRealTimers();
	});

	it.each(['Free', 'Pro', 'Team', 'Enterprise'])(
		'shows the Authorized Keys tab on the %s tier, with its form and its keys and no upgrade notice',
		async (tier) => {
			renderPage(tier);

			await fireEvent.click(screen.getByRole('tab', { name: 'Authorized Keys' }));

			const panel = within(screen.getByRole('tabpanel'));

			expect(await panel.findByLabelText('Signing Key')).toBeInTheDocument();
			expect(panel.getByRole('button', { name: 'Authorize' })).toBeInTheDocument();
			expect(panel.getByRole('cell', { name: 'Laptop' })).toBeInTheDocument();
			expect(panel.queryByText(teamNotice, { exact: false })).not.toBeInTheDocument();
			expect(panel.queryByRole('link', { name: 'Upgrade your subscription' })).not.toBeInTheDocument();
			expect(panel.queryByRole('alert')).not.toBeInTheDocument();
		}
	);

	it.each(['Free', 'Pro'])(
		'tells the %s tier what authorized keys are for: cluster reporting on every plan, other commands on Team',
		async (tier) => {
			api.getMachineAuthorizedKeys.mockResolvedValue([]);
			renderPage(tier);

			await fireEvent.click(screen.getByRole('tab', { name: 'Authorized Keys' }));

			const panel = within(screen.getByRole('tabpanel'));

			expect(await panel.findByText('No authorized keys')).toBeInTheDocument();
			expect(panel.getByText(/Signing keys authorized to sign commands for this machine/)).toHaveTextContent(
				'On every plan they sign cluster reporting commands; other remote commands need a Team subscription.'
			);
			expect(panel.getByText(/Authorize a signing key to sign cluster reporting commands/)).toBeInTheDocument();
			expect(panel.queryByText(/authorized to send remote commands/)).not.toBeInTheDocument();
			expect(panel.queryByText(/to enable remote commands/)).not.toBeInTheDocument();
		}
	);

	it.each(['Team', 'Enterprise'])('keeps the remote commands wording on the Authorized Keys tab for the %s tier', async (tier) => {
		api.getMachineAuthorizedKeys.mockResolvedValue([]);
		renderPage(tier);

		await fireEvent.click(screen.getByRole('tab', { name: 'Authorized Keys' }));

		const panel = within(screen.getByRole('tabpanel'));

		expect(await panel.findByText('No authorized keys')).toBeInTheDocument();
		expect(panel.getByText(/Signing keys authorized to send remote commands to this machine/)).toBeInTheDocument();
		expect(panel.getByText(/Authorize a signing key to enable remote commands/)).toBeInTheDocument();
		expect(panel.queryByText(/cluster reporting/)).not.toBeInTheDocument();
	});

	it('authorizes a signing key on the Pro tier', async () => {
		const secondKey: SigningKeyDto = { ...serverKey, id: 42, label: 'Desk', fingerprint: 'fp-b', publicKey: 'PUBLIC-B' };
		api.getSigningKeys.mockResolvedValue({ keys: [serverKey, secondKey], activeCount: 2, maxKeys: 5 });
		api.authorizeMachineKey.mockResolvedValue(undefined);
		renderPage('Pro');

		await fireEvent.click(screen.getByRole('tab', { name: 'Authorized Keys' }));
		await screen.findByRole('option', { name: /Desk/ });
		await fireEvent.change(screen.getByLabelText('Signing Key'), { target: { value: '42' } });
		await fireEvent.click(screen.getByRole('button', { name: 'Authorize' }));

		await waitFor(() => expect(api.authorizeMachineKey).toHaveBeenCalledWith(7, 42));
	});

	it.each(['Free', 'Pro'])('still shows the Team upgrade notice on the Commands tab on the %s tier', async (tier) => {
		renderPage(tier);

		await fireEvent.click(screen.getByRole('tab', { name: 'Commands' }));

		const notice = await within(screen.getByRole('tabpanel')).findByRole('alert');

		expect(notice).toHaveTextContent(teamNotice);
		expect(within(notice).getByRole('link', { name: 'Upgrade your subscription' })).toHaveAttribute(
			'href',
			'/settings/billing'
		);
		expect(screen.queryByRole('button', { name: 'Send' })).not.toBeInTheDocument();
	});

	it.each(['Team', 'Enterprise'])('gives the %s tier the Commands tab form without the notice', async (tier) => {
		renderPage(tier);

		await fireEvent.click(screen.getByRole('tab', { name: 'Commands' }));

		expect(await screen.findByRole('button', { name: 'Send' })).toBeInTheDocument();
		expect(screen.queryByText(teamNotice, { exact: false })).not.toBeInTheDocument();
	});
});
