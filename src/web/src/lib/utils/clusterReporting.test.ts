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
	describeBlockReason,
	describeReporterError,
	describeSendError,
	describeSkip,
	disableBlock,
	OFFLINE_HOST_ACCEPTED,
	OFFLINE_HOST_NOTE,
	planDisable,
	planEnable,
	planEnrolOne,
	planReenrol,
	planUnenrolOne,
	planUpdatePermissions,
	sendBlock,
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
		expect(assessMachine(machine({ authorizedSigningKeyIds: [41] }), [KEY], AGENT_ENROL)).toEqual({ key: KEY });
	});

	it('picks the first of the user keys that is authorised, not merely the first key', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [42] }), [KEY, OTHER_KEY], AGENT_ENROL)).toEqual({
			key: OTHER_KEY
		});
	});

	it('refuses a machine none of the user keys is authorised on', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [99] }), [KEY], AGENT_ENROL)).toEqual({
			skip: 'key_not_authorised'
		});
	});

	it('refuses every machine when the user has no usable key', () => {
		expect(assessMachine(machine(), [], AGENT_ENROL)).toEqual({ skip: 'key_not_authorised' });
	});

	it('refuses a paused machine, which the server would answer with a conflict', () => {
		expect(assessMachine(machine({ isPaused: true }), [KEY], AGENT_ENROL)).toEqual({ skip: 'paused' });
	});

	it('refuses to enrol a machine whose remote commands are disabled', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_ENROL)).toEqual({
			skip: 'remote_commands_disabled'
		});
	});

	it('still allows a machine whose remote commands are unknown, because unknown is not disabled', () => {
		const unknown = machine({ remoteCommandsEnabled: null });

		expect(assessMachine(unknown, [KEY], AGENT_ENROL)).toEqual({ key: KEY });
		expect(assessMachine(unknown, [KEY], AGENT_UNENROL)).toEqual({ key: KEY });
		expect(assessMachine(unknown, [KEY], AGENT_UNINSTALL, false)).toEqual({ key: KEY });
		expect(assessMachine(unknown, [KEY], AGENT_UNINSTALL, true)).toEqual({ key: KEY });
	});

	it('names the authorisation first when a machine is both unauthorised and paused', () => {
		expect(assessMachine(machine({ authorizedSigningKeyIds: [], isPaused: true }), [KEY], AGENT_ENROL)).toEqual({
			skip: 'key_not_authorised'
		});
	});

	it('unenrols a machine whose remote commands are disabled, since the host accepts cleanup', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_UNENROL)).toEqual({ key: KEY });
	});

	it('uninstalls through a machine whose remote commands are disabled when it is enrolled', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_UNINSTALL, true)).toEqual({ key: KEY });
	});

	it('refuses to uninstall through a machine whose remote commands are disabled and that never enrolled', () => {
		expect(assessMachine(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_UNINSTALL, false)).toEqual({
			skip: 'remote_commands_disabled'
		});
	});

	it('takes the enrolled flag the caller passes, not the machine entry, to judge an uninstall', () => {
		const reportsEnrolled = machine({ remoteCommandsEnabled: false, enrolled: true });

		expect(assessMachine(reportsEnrolled, [KEY], AGENT_UNINSTALL, false)).toEqual({ skip: 'remote_commands_disabled' });
	});

	it('uninstalls through a machine whose remote commands are enabled whether or not it is enrolled', () => {
		expect(assessMachine(machine(), [KEY], AGENT_UNINSTALL, false)).toEqual({ key: KEY });
		expect(assessMachine(machine(), [KEY], AGENT_UNINSTALL, true)).toEqual({ key: KEY });
	});

	it('keeps the authorisation and the pause rules for the cleanup commands', () => {
		const disabled = machine({ remoteCommandsEnabled: false });
		const unauthorised = { ...disabled, authorizedSigningKeyIds: [99] };
		const paused = { ...disabled, isPaused: true };

		expect(assessMachine(unauthorised, [KEY], AGENT_UNENROL)).toEqual({ skip: 'key_not_authorised' });
		expect(assessMachine(unauthorised, [KEY], AGENT_UNINSTALL, true)).toEqual({ skip: 'key_not_authorised' });
		expect(assessMachine(paused, [KEY], AGENT_UNENROL)).toEqual({ skip: 'paused' });
		expect(assessMachine(paused, [KEY], AGENT_UNINSTALL, true)).toEqual({ skip: 'paused' });
	});

	it('cannot judge an uninstall without being told whether the machine is enrolled', () => {
		const judgeWithoutSayingEnrolled = () =>
			// @ts-expect-error an uninstall needs the enrolled flag
			assessMachine(machine(), [KEY], AGENT_UNINSTALL);
		const blockWithoutSayingEnrolled = () =>
			// @ts-expect-error an uninstall needs the enrolled flag
			sendBlock(machine(), [KEY], AGENT_UNINSTALL);

		expect([judgeWithoutSayingEnrolled, blockWithoutSayingEnrolled]).toHaveLength(2);
	});
});

describe('sendBlock', () => {
	it('blocks an enrol on a machine whose remote commands are disabled, and nothing else on a clear machine', () => {
		expect(sendBlock(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_ENROL)).toBe('remote_commands_disabled');
		expect(sendBlock(machine(), [KEY], AGENT_ENROL)).toBeNull();
	});

	it('does not block an unenrol on a machine whose remote commands are disabled', () => {
		expect(sendBlock(machine({ remoteCommandsEnabled: false }), [KEY], AGENT_UNENROL)).toBeNull();
	});

	it('blocks an uninstall on a disabled machine only while it has not enrolled', () => {
		const disabled = machine({ remoteCommandsEnabled: false });

		expect(sendBlock(disabled, [KEY], AGENT_UNINSTALL, false)).toBe('remote_commands_disabled');
		expect(sendBlock(disabled, [KEY], AGENT_UNINSTALL, true)).toBeNull();
	});

	it('does not judge the key before the keys are known, or when the user has none, but judges the machine', () => {
		const unauthorised = machine({ authorizedSigningKeyIds: [99] });

		for (const keys of [null, []]) {
			expect(sendBlock(unauthorised, keys, AGENT_ENROL)).toBeNull();
			expect(sendBlock(machine({ isPaused: true }), keys, AGENT_UNENROL)).toBe('paused');
			expect(sendBlock(machine({ remoteCommandsEnabled: false }), keys, AGENT_ENROL)).toBe('remote_commands_disabled');
			expect(sendBlock(machine({ remoteCommandsEnabled: false }), keys, AGENT_UNENROL)).toBeNull();
			expect(sendBlock(machine({ remoteCommandsEnabled: false }), keys, AGENT_UNINSTALL, false)).toBe(
				'remote_commands_disabled'
			);
			expect(sendBlock(machine({ remoteCommandsEnabled: false }), keys, AGENT_UNINSTALL, true)).toBeNull();
		}
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

describe('planEnrolOne and remote commands', () => {
	it('plans nothing for a machine whose remote commands are disabled, because enrol needs them', () => {
		const plan = planEnrolOne(machine({ machineId: 5, machineName: 'cp-5', remoteCommandsEnabled: false }), [KEY]);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 5, machineName: 'cp-5', reason: 'remote_commands_disabled' }]);
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

	it('unenrols an online candidate whose remote commands are disabled, since the host accepts cleanup', () => {
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

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([
			[1, AGENT_UNINSTALL],
			[2, AGENT_UNENROL]
		]);
		expect(plan.skipped).toEqual([]);
	});

	it('uninstalls through the holder although its remote commands are disabled', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', remoteCommandsEnabled: false }),
					machine({ machineId: 2, machineName: 'cp-2' })
				],
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', isHolder: true }),
					candidate({ machineId: 2, machineName: 'cp-2' })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([
			[1, AGENT_UNINSTALL],
			[2, AGENT_UNENROL]
		]);
	});

	it('uninstalls through the only machine that can, a candidate with its remote commands disabled', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [machine({ machineId: 1, machineName: 'cp-1', remoteCommandsEnabled: false })],
				candidates: [candidate({ machineId: 1, machineName: 'cp-1' })]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[1, AGENT_UNINSTALL]]);
		expect(plan.skipped).toEqual([]);
	});

	it('uninstalls through a machine whose own report says it is enrolled although no candidate row exists yet', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', remoteCommandsEnabled: false, enrolled: true })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.commandType])).toEqual([[1, AGENT_UNINSTALL]]);
	});

	it('does not uninstall through a machine with remote commands disabled that never enrolled', () => {
		const plan = planDisable(
			context({
				eligibleMachines: [machine({ machineId: 9, machineName: 'cp-9', remoteCommandsEnabled: false })],
				candidates: []
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
	});

	it('ranks the holder, then an enrolled machine, then the rest, whatever the capability says', () => {
		const eligibleMachines = [
			machine({ machineId: 7, machineName: 'plain' }),
			machine({ machineId: 8, machineName: 'enrolled-disabled', remoteCommandsEnabled: false, enrolled: true }),
			machine({ machineId: 9, machineName: 'holder-disabled', remoteCommandsEnabled: false })
		];
		const ranked = (candidates: KubernetesCandidateDto[]) =>
			planDisable(context({ eligibleMachines, candidates }), [KEY]).commands[0].machineId;

		expect(ranked([candidate({ machineId: 9, machineName: 'holder-disabled', isHolder: true })])).toBe(9);
		expect(ranked([])).toBe(8);
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
	it('is clear when an eligible machine is online and can be sent the command', () => {
		expect(disableBlock(context({ eligibleMachines: [machine({ online: true })] }), [KEY])).toBeNull();
	});

	it('does not judge the key before the keys are known, or when the user has none', () => {
		const unauthorised = context({ eligibleMachines: [machine({ authorizedSigningKeyIds: [99] })] });

		expect(disableBlock(unauthorised, null)).toBeNull();
		expect(disableBlock(unauthorised, [])).toBeNull();
	});

	it('is clear when one machine can take the uninstall, whatever keeps the others from it', () => {
		const block = disableBlock(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', online: false }),
					machine({ machineId: 3, machineName: 'cp-3' })
				]
			}),
			[KEY]
		);

		expect(block).toBeNull();
	});

	it('names an offline machine as the reason when none is online', () => {
		const block = disableBlock(context({ eligibleMachines: [machine({ online: false })] }), [KEY]);

		expect(block?.summary).toBe(
			'Reporting can only be disabled through an online control-plane machine with an admin credential, and none can take the uninstall command right now.'
		);
		expect(block?.machines).toEqual([{ machineId: 1, machineName: 'cp-1', reason: 'offline' }]);
	});

	it('explains an online machine that cannot take the command instead of calling it available', () => {
		const block = disableBlock(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false }),
					machine({ machineId: 3, machineName: 'cp-3', authorizedSigningKeyIds: [99] }),
					machine({ machineId: 4, machineName: 'cp-4', online: false })
				]
			}),
			[KEY]
		);

		expect(block?.machines).toEqual([
			{ machineId: 1, machineName: 'cp-1', reason: 'paused' },
			{ machineId: 2, machineName: 'cp-2', reason: 'remote_commands_disabled' },
			{ machineId: 3, machineName: 'cp-3', reason: 'key_not_authorised' },
			{ machineId: 4, machineName: 'cp-4', reason: 'offline' }
		]);
	});

	it('still explains a paused or remote-disabled machine before the keys are known', () => {
		const block = disableBlock(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false })
				]
			}),
			null
		);

		expect(block?.machines.map((m) => m.reason)).toEqual(['paused', 'remote_commands_disabled']);
	});

	it('is clear when the only online machine has remote commands disabled but is a candidate', () => {
		const ctx = context({
			eligibleMachines: [machine({ remoteCommandsEnabled: false })],
			candidates: [candidate()]
		});

		expect(disableBlock(ctx, [KEY])).toBeNull();
		expect(disableBlock(ctx, null)).toBeNull();
	});

	it('is clear when the only online machine has remote commands disabled but reports itself enrolled', () => {
		const ctx = context({ eligibleMachines: [machine({ remoteCommandsEnabled: false, enrolled: true })] });

		expect(disableBlock(ctx, [KEY])).toBeNull();
	});

	it('still names remote commands for a disabled machine that never enrolled', () => {
		const block = disableBlock(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', remoteCommandsEnabled: false }),
					machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false, enrolled: true, isPaused: true })
				]
			}),
			[KEY]
		);

		expect(block?.machines).toEqual([
			{ machineId: 1, machineName: 'cp-1', reason: 'remote_commands_disabled' },
			{ machineId: 2, machineName: 'cp-2', reason: 'paused' }
		]);
	});

	it('gives the reason the command would be refused for first, and offline last', () => {
		const block = disableBlock(
			context({
				eligibleMachines: [
					machine({ machineId: 1, machineName: 'cp-1', authorizedSigningKeyIds: [], isPaused: true }),
					machine({ machineId: 2, machineName: 'cp-2', isPaused: true, online: false })
				]
			}),
			[KEY]
		);

		expect(block?.machines.map((m) => m.reason)).toEqual(['key_not_authorised', 'paused']);
	});

	it('explains with no machines to list when there is no eligible machine at all', () => {
		const block = disableBlock(context(), [KEY]);

		expect(block?.summary).toContain('none can take the uninstall command right now');
		expect(block?.machines).toEqual([]);
	});

	it('agrees with the disable plan: it blocks exactly when the plan would send nothing', () => {
		const cases: Array<Partial<KubernetesEligibleMachineDto>> = [
			{},
			{ online: false },
			{ isPaused: true },
			{ remoteCommandsEnabled: false },
			{ remoteCommandsEnabled: false, enrolled: true },
			{ remoteCommandsEnabled: null },
			{ authorizedSigningKeyIds: [99] }
		];

		for (const overrides of cases) {
			for (const isCandidate of [true, false]) {
				const ctx = context({
					eligibleMachines: [machine(overrides)],
					candidates: isCandidate ? [candidate({ online: overrides.online ?? true })] : []
				});
				const sendsNothing = planDisable(ctx, [KEY]).commands.length === 0;

				expect(disableBlock(ctx, [KEY]) !== null, JSON.stringify({ overrides, isCandidate })).toBe(sendsNothing);
			}
		}
	});
});

describe('describeBlockReason', () => {
	it('words a paused, remote-disabled and unauthorised machine as the rows do', () => {
		expect(describeBlockReason('paused')).toBe(describeSkip('paused'));
		expect(describeBlockReason('remote_commands_disabled')).toBe(describeSkip('remote_commands_disabled'));
		expect(describeBlockReason('key_not_authorised')).toBe(describeSkip('key_not_authorised'));
	});

	it('words an offline machine as a state, since nothing was about to be sent to it', () => {
		expect(describeBlockReason('offline')).toBe('Offline');
	});
});

describe('the offline host wording', () => {
	it('says the command must reach the host within the five minutes the agent accepts it for', () => {
		expect(OFFLINE_HOST_NOTE).toBe('Offline. The host must receive the command within 5 minutes, or it will refuse it.');
		expect(OFFLINE_HOST_ACCEPTED).toBe(
			'Accepted. The host is offline, and it will refuse the command unless it receives it within 5 minutes.'
		);
	});

	it('does not promise the ten minutes the command is signed to be valid for', () => {
		expect(OFFLINE_HOST_NOTE).not.toContain('10 minutes');
		expect(OFFLINE_HOST_ACCEPTED).not.toContain('10 minutes');
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

	it('plans an unenrol for a candidate whose remote commands are disabled', () => {
		const plan = planUnenrolOne(
			context({ eligibleMachines: [machine({ machineId: 2, machineName: 'cp-2', remoteCommandsEnabled: false })] }),
			candidate({ machineId: 2, machineName: 'cp-2' }),
			[KEY]
		);

		expect(plan.commands).toEqual([
			{ machineId: 2, machineName: 'cp-2', commandType: AGENT_UNENROL, localKeyId: 3, online: true }
		]);
		expect(plan.skipped).toEqual([]);
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

describe('planReenrol and remote commands', () => {
	it('skips an affected candidate whose remote commands are disabled, because enrol needs them', () => {
		const plan = planReenrol(
			context({
				reporterError: 'credentials_rejected',
				eligibleMachines: [machine({ machineId: 1, remoteCommandsEnabled: false })],
				candidates: [candidate({ machineId: 1, reporterError: 'credentials_rejected' })]
			}),
			[KEY]
		);

		expect(plan.commands).toEqual([]);
		expect(plan.skipped).toEqual([{ machineId: 1, machineName: 'cp-1', reason: 'remote_commands_disabled' }]);
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

	it('prefers an online candidate to an offline holder', () => {
		const plan = planUpdatePermissions(
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

		expect(plan.commands.map((c) => [c.machineId, c.online])).toEqual([[2, true]]);
	});

	it('still prefers the holder among online candidates, and among offline ones when none is online', () => {
		const eligibleMachines = [
			machine({ machineId: 1, machineName: 'cp-1', online: false }),
			machine({ machineId: 2, machineName: 'cp-2', online: false })
		];
		const plan = planUpdatePermissions(
			context({
				eligibleMachines,
				candidates: [
					candidate({ machineId: 1, machineName: 'cp-1', online: false }),
					candidate({ machineId: 2, machineName: 'cp-2', isHolder: true, online: false })
				]
			}),
			[KEY]
		);

		expect(plan.commands.map((c) => [c.machineId, c.online])).toEqual([[2, false]]);
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

describe('describeReporterError', () => {
	it('words the five codes a reporter reports', () => {
		expect(describeReporterError('credentials_rejected')).toBe('Credentials rejected');
		expect(describeReporterError('tls_ca_changed')).toBe('Cluster CA changed');
		expect(describeReporterError('state_copied')).toBe(
			'Reporter state was copied from another host; unenrol or re-enrol this host'
		);
		expect(describeReporterError('api_unreachable')).toBe('API server unreachable');
		expect(describeReporterError('namespace_terminating')).toBe('The vord-system namespace is terminating');
	});

	it('makes a readable label of a code it has no wording for', () => {
		expect(describeReporterError('kubeconfig_unreadable')).toBe('Kubeconfig unreadable');
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

	it('shows the server message of a 403 as it is, with no wording of its own added', () => {
		expect(describeSendError(new ApiError(403, 'Remote commands require a Team subscription'))).toBe(
			'Remote commands require a Team subscription'
		);
	});

	it('shows a bare Forbidden as Forbidden, without guessing at a Team subscription', () => {
		expect(describeSendError(new ApiError(403, 'Forbidden'))).toBe('Forbidden');
	});

	it('shows the message of any other error, and a fallback for a non-error', () => {
		expect(describeSendError(new Error('Selected key not found.'))).toBe('Selected key not found.');
		expect(describeSendError('boom')).toBe('Failed to send the command');
	});
});
