// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { ApiError } from '$lib/api/client';
import type { KubernetesCandidateDto, KubernetesEligibleMachineDto } from '$lib/api/types';
import type { UsableSigningKey } from '$lib/commands/signedCommand';
import {
	AGENT_ENROL,
	AGENT_UNENROL,
	AGENT_UNINSTALL,
	assessMachine,
	commandVerb,
	describeAgentMessage,
	describeSendError,
	describeSkip,
	disableBlock,
	planDisable,
	planEnable,
	planEnrolOne,
	planReenrol,
	planUnenrolOne,
	planUpdatePermissions,
	type ReportingContext
} from './clusterReporting';

const KEY: UsableSigningKey = { localKeyId: 3, serverKeyId: 41, label: 'Laptop' };
const OTHER_KEY: UsableSigningKey = { localKeyId: 4, serverKeyId: 42, label: 'Desk' };

function machine(overrides: Partial<KubernetesEligibleMachineDto> = {}): KubernetesEligibleMachineDto {
	return {
		machineId: 1,
		machineName: 'cp-1',
		online: true,
		isPaused: false,
		remoteCommandsEnabled: true,
		enrolled: false,
		authorizedSigningKeyIds: [41],
		...overrides
	};
}

function candidate(overrides: Partial<KubernetesCandidateDto> = {}): KubernetesCandidateDto {
	return {
		machineId: 1,
		machineName: 'cp-1',
		isHolder: false,
		reporterError: null,
		lastReportAt: '2026-10-12T09:00:00Z',
		online: true,
		...overrides
	};
}

function context(overrides: Partial<ReportingContext> = {}): ReportingContext {
	return {
		candidates: [],
		eligibleMachines: [],
		activeReporterMachineId: null,
		reporterError: null,
		...overrides
	};
}

describe('assessMachine', () => {
	it('picks a key authorised on the machine', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [41] }), [KEY])).toEqual({ key: KEY });
	});

	it('picks the first of the user keys that is authorised, not merely the first key', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [42] }), [KEY, OTHER_KEY])).toEqual({ key: OTHER_KEY });
	});

	it('refuses a machine none of the user keys is authorised on', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [99] }), [KEY])).toEqual({ skip: 'key_not_authorised' });
	});

	it('refuses every machine when the user has no usable key', () => {
		expect(assessMachine(machine(), [])).toEqual({ skip: 'key_not_authorised' });
	});

	it('refuses a paused machine, which the server would answer with a conflict', () => {
		expect(assessMachine(machine({ isPaused: true }), [KEY])).toEqual({ skip: 'paused' });
	});

	it('refuses a machine whose remote commands are disabled', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: false }), [KEY])).toEqual({ skip: 'remote_commands_disabled' });
	});

	it('still allows a machine whose remote commands are unknown, because unknown is not disabled', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: null }), [KEY])).toEqual({ key: KEY });
	});

	it('names the authorisation first when a machine is both unauthorised and paused', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [], isPaused: true }), [KEY])).toEqual({
			skip: 'key_not_authorised'
		});
	});
});

describe('planEnable', () => {
	it('enrols every eligible machine the key is authorised on', () => {
		const plan = planEnable(
			context({
				eligibleMachines: [machine({ machineId: 1, machineName: 'cp-1' }), machine({ machineId: 2, machineName: 'cp-2' })]
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([
			{ machineId: 1, machineName: 'cp-1', commandType: AGENT_ENROL, localKeyId: 3, online: true },
			{ machineId: 2, machineName: 'cp-2', commandType: AGENT_ENROL, localKeyId: 3, online: true }
		]);
		expect(plan.skipped).toEqual([]);
	});

	it('skips the machines the key is not authorised on and lists them', () => {
		const plan = planEnable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1' }),
					machine({ machineId: 2, machineName: 'cp-2', authorizedSigningKeyIds: [] })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([1]);
		expect(plan.skipped).toEqual([{ machineId: 2, machineName: 'cp-2', reason: 'key_not_authorised' }]);
	});

	it('skips paused machines and machines with remote commands disabled, each for its own reason', () => {
		const plan = planEnable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false }),
					machine({ machineId: 3, machineName: 'cp-3' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([3]);
		expect(plan.skipped).toEqual([
			{ machineId: 1, machineName: 'cp-1', reason: 'paused' },
			{ machineId: 2, machineName: 'cp-2', reason: 'remote_commands_disabled' }
		]);
	});

	it('leaves out a machine that is already enrolled or already a candidate', () => {
		const plan = planEnable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, enrolled: true }),
					machine({ machineId: 2 }),
					machine({ machineId: 3 })
				],
				candidates: [candidate({ machineId: 2 })]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([3]);
		expect(plan.skipped).toEqual([]);
	});

	it('remembers whether the target was offline, so the result can say the command may expire', () => {
		const plan = planEnable(context({ eligibleMachines: [machine({ online: false })] }), [KEY]);

		expect(plan.commands[0].online).toBe(false);
	});
});

describe('planEnrolOne', () => {
	it('plans one enrol for a machine the key is authorised on', () => {
		const plan = planEnrolOne(machine({ machineId: 5, machineName: 'cp-5' }), [KEY]);

		expect(plan.commands).toEqual([
			{ machineId: 5, machineName: 'cp-5', commandType: AGENT_ENROL, localKeyId: 3, online: true }
		]);
		expect(plan.skipped).toEqual([]);
	});

	it('plans nothing for a machine that cannot be sent a command, and says why', () => {
		const plan = planEnrolOne(machine({ machineId: 5, machineName: 'cp-5', isPaused: true }), [KEY]);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 5, machineName: 'cp-5', reason: 'paused' }]);
	});
});

describe('planDisable', () => {
	it('uninstalls through the holder and unenrols every other online candidate', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1' }),
					machine({ machineId: 2, machineName: 'cp-2' }),
					machine({ machineId: 3, machineName: 'cp-3' })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1' }),
					candidate({ machineId: 2, machineName: 'cp-2', isHolder: true }),
					candidate({ machineId: 3, machineName: 'cp-3' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([
			[2, AGENT_UNINSTALL],
			[1, AGENT_UNENROL],
			[3, AGENT_UNENROL]
		]);
		expect(plan.skipped).toEqual([]);
	});

	it('reads the holder from the cluster when the candidate rows do not mark one', () => {
		const plan = planDisable(
			context({
				activeReporterMachineId: 3,
				eligibleMachines: [machine({ machineId: 1 }), machine({ machineId: 3, machineName: 'cp-3' })],
				candidates: [candidate({ machineId: 1 }), candidate({ machineId: 3, machineName: 'cp-3' })]
			}),
			[KEY]
		);

		expect(plan.commands[0]).toMatchObject({ machineId: 3, commandType: AGENT_UNINSTALL });
	});

	it('falls back to another online eligible machine when the holder is offline', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', online: false }),
					machine({ machineId: 2, machineName: 'cp-2' })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true, online: false }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[2, AGENT_UNINSTALL]]);
		expect(plan.skipped).toEqual([{ machineId: 1, machineName: 'cp-1', reason: 'offline' }]);
	});

	it('uninstalls through an eligible machine that is not a candidate when no candidate can do it', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', online: false }),
					machine({ machineId: 9, machineName: 'cp-9' })
				],
				candidates: [candidate({ machineId: 1, machineName: 'cp-1', online: false })]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[9, AGENT_UNINSTALL]]);
	});

	it('skips an online candidate that cannot be sent a command and says why', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1' }),
					machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([1]);
		expect(plan.skipped).toEqual([{ machineId: 2, machineName: 'cp-2', reason: 'remote_commands_disabled' }]);
	});

	it('does not uninstall through a paused or unauthorised machine', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', authorizedSigningKeyIds: [] })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([
			{ machineId: 1, machineName: 'cp-1', reason: 'paused' },
			{ machineId: 2, machineName: 'cp-2', reason: 'key_not_authorised' }
		]);
	});

	it('sends an unenrol to an online candidate the eligibility list does not describe, using the first key', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [machine({ machineId: 1, machineName: 'cp-1' })],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true }),
					candidate({ machineId: 8, machineName: 'old-cp' })
				]
			}),
			[KEY, OTHER_KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType, c.localKeyId])).toEqual([
			[1, AGENT_UNINSTALL, 3],
			[8, AGENT_UNENROL, 3]
		]);
	});
});

describe('disableBlock', () => {
	it('is clear when an eligible machine is online', () => {
		expect(disableBlock(context({ eligibleMachines: [machine({ online: true })] }))).toBeNull();
	});

	it('explains why when no eligible machine is online', () => {
		expect(disableBlock(context({ eligibleMachines: [machine({ online: false })] }))).toBe(
			'Reporting can only be disabled through a control-plane machine with an admin credential, and none is online right now.'
		);
	});

	it('explains the same when there is no eligible machine at all', () => {
		expect(disableBlock(context())).toContain('none is online right now');
	});
});

describe('planUnenrolOne', () => {
	it('plans an unenrol for a candidate that is authorised', () => {
		const plan = planUnenrolOne(
			context({ eligibleMachines: [machine({ machineId: 2, machineName: 'cp-2' })] }),
			candidate({ machineId: 2, machineName: 'cp-2' }),
			[KEY]
		);

		expect(plan.commands).toEqual([
			{ machineId: 2, machineName: 'cp-2', commandType: AGENT_UNENROL, localKeyId: 3, online: true }
		]);
	});

	it('plans nothing for a candidate whose host is paused', () => {
		const plan = planUnenrolOne(
			context({ eligibleMachines: [machine({ machineId: 2, machineName: 'cp-2', isPaused: true })] }),
			candidate({ machineId: 2, machineName: 'cp-2' }),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 2, machineName: 'cp-2', reason: 'paused' }]);
	});

	it('lets the server decide for a candidate the eligibility list does not describe', () => {
		const plan = planUnenrolOne(context(), candidate({ machineId: 8, machineName: 'old-cp', online: false }), [KEY]);

		expect(plan.commands).toEqual([
			{ machineId: 8, machineName: 'old-cp', commandType: AGENT_UNENROL, localKeyId: 3, online: false }
		]);
	});

	it('plans nothing when the user has no usable key', () => {
		const plan = planUnenrolOne(context(), candidate({ machineId: 8, machineName: 'old-cp' }), []);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 8, machineName: 'old-cp', reason: 'key_not_authorised' }]);
	});
});

describe('planReenrol', () => {
	it('enrols again the candidates that report the cluster error', () => {
		const plan = planReenrol(
			context({
				reporterError: 'credentials_rejected',
				eligibleMachines: [machine({ machineId: 1, machineName: 'cp-1' }), machine({ machineId: 2, machineName: 'cp-2' })],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', reporterError: 'credentials_rejected' }),
					candidate({ machineId: 2, machineName: 'cp-2', reporterError: null })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[1, AGENT_ENROL]]);
	});

	it('falls back to every candidate when none reports the error itself', () => {
		const plan = planReenrol(
			context({
				reporterError: 'tls_ca_changed',
				eligibleMachines: [machine({ machineId: 1 }), machine({ machineId: 2, machineName: 'cp-2' })],
				candidates: [candidate({ machineId: 1 }), candidate({ machineId: 2, machineName: 'cp-2' })]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([1, 2]);
	});

	it('skips an affected candidate that is not an eligible machine, since it could not enrol', () => {
		const plan = planReenrol(
			context({
				reporterError: 'credentials_rejected',
				eligibleMachines: [machine({ machineId: 1 })],
				candidates: [
					candidate({ machineId: 1, reporterError: 'credentials_rejected' }),
					candidate({ machineId: 8, machineName: 'old-cp', reporterError: 'credentials_rejected' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([1]);
		expect(plan.skipped).toEqual([{ machineId: 8, machineName: 'old-cp', reason: 'not_eligible' }]);
	});

	it('skips an affected candidate that cannot be sent a command', () => {
		const plan = planReenrol(
			context({
				reporterError: 'credentials_rejected',
				eligibleMachines: [machine({ machineId: 1, isPaused: true })],
				candidates: [candidate({ machineId: 1, reporterError: 'credentials_rejected' })]
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 1, machineName: 'cp-1', reason: 'paused' }]);
	});
});

describe('planUpdatePermissions', () => {
	it('enrols once, through the holder', () => {
		const plan = planUpdatePermissions(
			context({
				eligibleMachines: [machine({ machineId: 1 }), machine({ machineId: 2, machineName: 'cp-2' })],
				candidates: [candidate({ machineId: 1 }), candidate({ machineId: 2, machineName: 'cp-2', isHolder: true })]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[2, AGENT_ENROL]]);
	});

	it('uses another eligible candidate when the holder cannot be sent a command', () => {
		const plan = planUpdatePermissions(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2' })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([2]);
	});

	it('prefers an online candidate to an offline one', () => {
		const plan = planUpdatePermissions(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', online: false }),
					machine({ machineId: 2, machineName: 'cp-2' })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', online: false }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => c.machineId)).toEqual([2]);
	});

	it('plans nothing, and says why, when no holder or candidate can be sent a command', () => {
		const plan = planUpdatePermissions(
			context({
				eligibleMachines: [machine({ machineId: 1, machineName: 'cp-1', remoteCommandsEnabled: false })],
				candidates: [candidate({ machineId: 1, machineName: 'cp-1', isHolder: true })]
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 1, machineName: 'cp-1', reason: 'remote_commands_disabled' }]);
	});

	it('does not send to an eligible machine that is neither the holder nor a candidate', () => {
		const plan = planUpdatePermissions(
			context({ eligibleMachines: [machine({ machineId: 9 })], candidates: [candidate({ machineId: 1 })] }),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
	});
});

describe('commandVerb', () => {
	it('names the three commands for a person', () => {
		expect(commandVerb(AGENT_ENROL)).toBe('Enrol');
		expect(commandVerb(AGENT_UNENROL)).toBe('Unenrol');
		expect(commandVerb(AGENT_UNINSTALL)).toBe('Uninstall');
	});
});

describe('describeSkip', () => {
	it('words the remote commands reason the way the agent config names it', () => {
		expect(describeSkip('remote_commands_disabled')).toBe(
			'Remote commands are disabled on this host (allow_remote_commands in the agent config)'
		);
	});

	it('words the other reasons', () => {
		expect(describeSkip('paused')).toBe('This host is paused');
		expect(describeSkip('key_not_authorised')).toBe('Your signing key is not authorised on this machine');
		expect(describeSkip('offline')).toBe('Offline, so it was not sent a command');
		expect(describeSkip('not_eligible')).toBe('Not a control-plane machine with an admin credential');
	});
});

describe('describeAgentMessage', () => {
	it('words a bare code', () => {
		expect(describeAgentMessage('credentials_rejected')).toEqual({
			code: 'credentials_rejected',
			label: 'Credentials rejected',
			detail: null
		});
	});

	it('splits a code from its detail at the first colon only', () => {
		expect(describeAgentMessage('api_unreachable: dial tcp 10.0.0.1:6443: i/o timeout')).toEqual({
			code: 'api_unreachable',
			label: 'API server unreachable',
			detail: 'dial tcp 10.0.0.1:6443: i/o timeout'
		});
	});

	it('words the four reporter errors', () => {
		expect(describeAgentMessage('tls_ca_changed').label).toBe('Cluster CA changed');
		expect(describeAgentMessage('namespace_terminating').label).toBe('The vord-system namespace is terminating');
	});

	it('makes a readable label of a code it has no wording for, and keeps the code', () => {
		expect(describeAgentMessage('kubeconfig_unreadable: permission denied')).toEqual({
			code: 'kubeconfig_unreadable',
			label: 'Kubeconfig unreadable',
			detail: 'permission denied'
		});
	});

	it('treats an empty detail as none', () => {
		expect(describeAgentMessage('credentials_rejected:  ').detail).toBeNull();
	});
});

describe('describeSendError', () => {
	it('shows the API message', () => {
		expect(describeSendError(new ApiError(400, 'Only control-plane machines with an admin credential can enrol'))).toBe(
			'Only control-plane machines with an admin credential can enrol'
		);
	});

	it('shows the pause message of a conflict', () => {
		expect(describeSendError(new ApiError(409, 'This host is paused'))).toBe('This host is paused');
	});

	it('explains the bare Forbidden the API client reduces every 403 to', () => {
		expect(describeSendError(new ApiError(403, 'Forbidden'))).toBe(
			'Forbidden. Remote commands require a Team subscription and a signing key authorised on this host.'
		);
	});

	it('shows a 403 message the client did carry through', () => {
		expect(describeSendError(new ApiError(403, 'Remote commands require a Team subscription'))).toBe(
			'Remote commands require a Team subscription'
		);
	});

	it('shows the message of any other error, and a fallback for a non-error', () => {
		expect(describeSendError(new Error('Selected key not found.'))).toBe('Selected key not found.');
		expect(describeSendError('boom')).toBe('Failed to send the command');
	});
});
