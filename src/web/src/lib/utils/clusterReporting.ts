// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type {
	KubernetesCandidateDto,
	KubernetesClusterDetailDto,
	KubernetesEligibleMachineDto
} from '$lib/api/types';
import type { UsableSigningKey } from '$lib/commands/signedCommand';

export const AGENT_ENROL = 'k8s_reporter_enrol';
export const AGENT_UNENROL = 'k8s_reporter_unenrol';
export const AGENT_UNINSTALL = 'k8s_reporter_uninstall';

export type ReporterCommandType = typeof AGENT_ENROL | typeof AGENT_UNENROL | typeof AGENT_UNINSTALL;

/** Why a machine is not sent a command. */
export type SkipReason =
	| 'key_not_authorised'
	| 'paused'
	| 'remote_commands_disabled'
	| 'offline'
	| 'not_eligible';

/** The part of a cluster the plans read. */
export type ReportingContext = Pick<
	KubernetesClusterDetailDto,
	'candidates' | 'eligibleMachines' | 'activeReporterMachineId' | 'reporterError'
>;

export interface PlannedCommand {
	machineId: number;
	machineName: string;
	commandType: ReporterCommandType;
	/** The local key the command is signed with. */
	localKeyId: number;
	/** Whether the machine was online when the plan was made. */
	online: boolean;
}

export interface SkippedMachine {
	machineId: number;
	machineName: string;
	reason: SkipReason;
}

export interface ReportingPlan {
	commands: PlannedCommand[];
	skipped: SkippedMachine[];
}

export interface CommandResult {
	machineId: number;
	machineName: string;
	commandType: ReporterCommandType;
	accepted: boolean;
	message: string;
}

export type MachineAssessment = { key: UsableSigningKey } | { skip: SkipReason };

// Enrolling needs the remote commands capability. Unenrol and uninstall are cleanup, which a host
// accepts with the capability off, but an uninstall only reaches such a host when it once enrolled:
// the server wants a candidate row or a report that says so, and the agent wants its reporter record.
// A machine whose capabilities could not be read (null) is allowed: unknown is not disabled.
function lacksCapabilityFor(
	machine: KubernetesEligibleMachineDto,
	commandType: ReporterCommandType,
	enrolled: boolean | undefined
): boolean {
	if (machine.remoteCommandsEnabled !== false) {
		return false;
	}

	return commandType === AGENT_ENROL || (commandType === AGENT_UNINSTALL && enrolled !== true);
}

/** The commands whose capability rule does not depend on whether the machine is enrolled. */
type EnrolmentIndependentCommand = typeof AGENT_ENROL | typeof AGENT_UNENROL;

/**
 * Decides whether a machine can be sent a command and with which of the user's keys. The order of
 * the reasons follows the server's: the key is checked before the pause, and the pause before the
 * capability. The capability only counts for the commands that need it (see lacksCapabilityFor).
 * An uninstall must be told whether the machine is known to be enrolled, which is the caller's to
 * decide because a candidate row of the cluster counts as well as the machine's own report (see
 * isEnrolled); the other commands do not take the flag.
 */
export function assessMachine(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[],
	commandType: EnrolmentIndependentCommand
): MachineAssessment;
export function assessMachine(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[],
	commandType: typeof AGENT_UNINSTALL,
	enrolled: boolean
): MachineAssessment;
export function assessMachine(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[],
	commandType: ReporterCommandType,
	enrolled?: boolean
): MachineAssessment {
	const key = keys.find((k) => machine.authorizedSigningKeyIds.includes(k.serverKeyId));
	if (key === undefined) {
		return { skip: 'key_not_authorised' };
	}
	if (machine.isPaused) {
		return { skip: 'paused' };
	}
	if (lacksCapabilityFor(machine, commandType, enrolled)) {
		return { skip: 'remote_commands_disabled' };
	}

	return { key };
}

/**
 * Why a machine cannot be sent a command, or null when it can, for the places that explain it before
 * anything is planned. Without keys to check (null while they load, empty when the user has none) the
 * authorisation is not judged, because the notice about the missing key says so once; a pause and a
 * disabled capability are facts about the machine and always count.
 */
export function sendBlock(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[] | null,
	commandType: EnrolmentIndependentCommand
): SkipReason | null;
export function sendBlock(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[] | null,
	commandType: typeof AGENT_UNINSTALL,
	enrolled: boolean
): SkipReason | null;
export function sendBlock(
	machine: KubernetesEligibleMachineDto,
	keys: UsableSigningKey[] | null,
	commandType: ReporterCommandType,
	enrolled?: boolean
): SkipReason | null {
	if (keys === null || keys.length === 0) {
		if (machine.isPaused) {
			return 'paused';
		}

		return lacksCapabilityFor(machine, commandType, enrolled) ? 'remote_commands_disabled' : null;
	}

	const assessment =
		commandType === AGENT_UNINSTALL
			? assessMachine(machine, keys, commandType, enrolled === true)
			: assessMachine(machine, keys, commandType);

	return 'skip' in assessment ? assessment.skip : null;
}

function command(
	machineId: number,
	machineName: string,
	commandType: ReporterCommandType,
	key: UsableSigningKey,
	online: boolean
): PlannedCommand {
	return { machineId, machineName, commandType, localKeyId: key.localKeyId, online };
}

function emptyPlan(): ReportingPlan {
	return { commands: [], skipped: [] };
}

function holderId(context: ReportingContext): number | null {
	return context.candidates.find((c) => c.isHolder)?.machineId ?? context.activeReporterMachineId;
}

function eligibleById(context: ReportingContext): Map<number, KubernetesEligibleMachineDto> {
	return new Map(context.eligibleMachines.map((m) => [m.machineId, m]));
}

function isCandidate(context: ReportingContext, machineId: number): boolean {
	return context.candidates.some((c) => c.machineId === machineId);
}

// A machine is known to be enrolled when the cluster lists it as a candidate or its own latest report
// says so. This is the evidence the server wants before it uninstalls through a host with remote
// commands off.
function isEnrolled(context: ReportingContext, machine: KubernetesEligibleMachineDto): boolean {
	return machine.enrolled || isCandidate(context, machine.machineId);
}

/** Enrols every eligible machine that is not enrolled yet and can be sent the command. */
export function planEnable(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	for (const machine of context.eligibleMachines) {
		if (isEnrolled(context, machine)) {
			continue;
		}

		addEnrol(plan, machine, keys);
	}

	return plan;
}

export function planEnrolOne(machine: KubernetesEligibleMachineDto, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	addEnrol(plan, machine, keys);

	return plan;
}

function addEnrol(plan: ReportingPlan, machine: KubernetesEligibleMachineDto, keys: UsableSigningKey[]): void {
	const assessment = assessMachine(machine, keys, AGENT_ENROL);
	if ('key' in assessment) {
		plan.commands.push(command(machine.machineId, machine.machineName, AGENT_ENROL, assessment.key, machine.online));
	} else {
		plan.skipped.push({ machineId: machine.machineId, machineName: machine.machineName, reason: assessment.skip });
	}
}

/** Why reporting cannot be disabled right now: what is needed, and what keeps each eligible machine from providing it. */
export interface DisableBlock {
	summary: string;
	machines: SkippedMachine[];
}

const disableBlockSummary =
	'Reporting can only be disabled through an online control-plane machine with an admin credential, and none can take the uninstall command right now.';

/**
 * Explains why reporting cannot be disabled, or returns null when an online eligible machine can be
 * sent the uninstall. The reasons follow the order the command is refused in (key, pause, capability)
 * and offline comes last, so a machine reads as unavailable for the first thing that has to change.
 * The capability only keeps a machine out when it never enrolled: a host that did accepts the cleanup
 * with remote commands off.
 */
export function disableBlock(context: ReportingContext, keys: UsableSigningKey[] | null): DisableBlock | null {
	const machines: SkippedMachine[] = [];
	for (const machine of context.eligibleMachines) {
		const reason =
			sendBlock(machine, keys, AGENT_UNINSTALL, isEnrolled(context, machine)) ?? (machine.online ? null : 'offline');
		if (reason === null) {
			return null;
		}

		machines.push({ machineId: machine.machineId, machineName: machine.machineName, reason });
	}

	return { summary: disableBlockSummary, machines };
}

/**
 * Uninstalls through one online eligible machine, preferring the holder, then a machine known to be
 * enrolled, and unenrols every other online candidate. A machine with remote commands off is only a
 * target once it has enrolled. Without a machine to uninstall through nothing is sent: an unenrol
 * alone would drop the hosts' tokens and leave the cluster objects behind.
 */
export function planDisable(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	const holder = holderId(context);

	const viable = context.eligibleMachines
		.filter((m) => m.online)
		.map((machine) => ({
			machine,
			assessment: assessMachine(machine, keys, AGENT_UNINSTALL, isEnrolled(context, machine))
		}))
		.filter((entry): entry is { machine: KubernetesEligibleMachineDto; assessment: { key: UsableSigningKey } } =>
			'key' in entry.assessment
		);
	const rank = (machine: KubernetesEligibleMachineDto): number =>
		machine.machineId === holder ? 0 : isEnrolled(context, machine) ? 1 : 2;
	viable.sort((a, b) => rank(a.machine) - rank(b.machine));
	const target = viable.at(0);

	const described = eligibleById(context);
	const unenrols: PlannedCommand[] = [];
	for (const candidate of context.candidates) {
		if (target !== undefined && candidate.machineId === target.machine.machineId) {
			continue;
		}

		if (candidate.online === false) {
			plan.skipped.push({ machineId: candidate.machineId, machineName: candidate.machineName, reason: 'offline' });
			continue;
		}

		addUnenrol(plan, unenrols, candidate, described.get(candidate.machineId), keys);
	}

	if (target !== undefined) {
		plan.commands.push(
			command(target.machine.machineId, target.machine.machineName, AGENT_UNINSTALL, target.assessment.key, true),
			...unenrols
		);
	}

	return plan;
}

export function planUnenrolOne(
	context: ReportingContext,
	candidate: KubernetesCandidateDto,
	keys: UsableSigningKey[]
): ReportingPlan {
	const plan = emptyPlan();
	addUnenrol(plan, plan.commands, candidate, eligibleById(context).get(candidate.machineId), keys);

	return plan;
}

// A candidate the eligibility list does not describe (its last report no longer shows an admin
// credential, say) has no authorised-key list to check, so it is tried with the first key and the
// server decides.
function addUnenrol(
	plan: ReportingPlan,
	into: PlannedCommand[],
	candidate: KubernetesCandidateDto,
	described: KubernetesEligibleMachineDto | undefined,
	keys: UsableSigningKey[]
): void {
	const assessment: MachineAssessment =
		described !== undefined
			? assessMachine(described, keys, AGENT_UNENROL)
			: keys.length > 0
				? { key: keys[0] }
				: { skip: 'key_not_authorised' };

	if ('key' in assessment) {
		into.push(command(candidate.machineId, candidate.machineName, AGENT_UNENROL, assessment.key, candidate.online));
	} else {
		plan.skipped.push({ machineId: candidate.machineId, machineName: candidate.machineName, reason: assessment.skip });
	}
}

/**
 * Enrols again the candidates affected by a cluster error: those that report it themselves, or every
 * candidate when none does. Enrol needs a control-plane machine with an admin credential, so a
 * candidate the eligibility list does not describe is skipped.
 */
export function planReenrol(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	const reporting = context.candidates.filter((c) => c.reporterError === context.reporterError);
	const affected = reporting.length > 0 ? reporting : context.candidates;
	const described = eligibleById(context);

	for (const candidate of affected) {
		const machine = described.get(candidate.machineId);
		if (machine === undefined) {
			plan.skipped.push({ machineId: candidate.machineId, machineName: candidate.machineName, reason: 'not_eligible' });
			continue;
		}

		addEnrol(plan, machine, keys);
	}

	return plan;
}

/**
 * Enrols once, through an online candidate if one can be sent the command, preferring the holder among
 * those, and otherwise through an offline one, again preferring the holder.
 */
export function planUpdatePermissions(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	const holder = holderId(context);
	const described = eligibleById(context);

	const considered = context.candidates
		.map((candidate) => ({ candidate, machine: described.get(candidate.machineId) }))
		.filter((entry): entry is { candidate: KubernetesCandidateDto; machine: KubernetesEligibleMachineDto } =>
			entry.machine !== undefined
		);
	const rank = (candidate: KubernetesCandidateDto): number =>
		(candidate.online ? 0 : 2) + (candidate.machineId === holder ? 0 : 1);
	considered.sort((a, b) => rank(a.candidate) - rank(b.candidate));

	for (const { candidate, machine } of considered) {
		const assessment = assessMachine(machine, keys, AGENT_ENROL);
		if ('key' in assessment) {
			plan.skipped.length = 0;
			plan.commands.push(command(machine.machineId, machine.machineName, AGENT_ENROL, assessment.key, candidate.online));

			return plan;
		}

		plan.skipped.push({ machineId: machine.machineId, machineName: machine.machineName, reason: assessment.skip });
	}

	return plan;
}

const commandWording: Record<ReporterCommandType, string> = {
	[AGENT_ENROL]: 'Enrol',
	[AGENT_UNENROL]: 'Unenrol',
	[AGENT_UNINSTALL]: 'Uninstall'
};

export function commandVerb(commandType: ReporterCommandType): string {
	return commandWording[commandType];
}

const skipWording: Record<SkipReason, string> = {
	key_not_authorised: 'Your signing key is not authorised on this machine',
	paused: 'This host is paused',
	remote_commands_disabled: 'Remote commands are disabled on this host (allow_remote_commands in the agent config)',
	offline: 'Offline, so it was not sent a command',
	not_eligible: 'Not a control-plane machine with an admin credential'
};

export function describeSkip(reason: SkipReason): string {
	return skipWording[reason];
}

/** Words the reason a machine is listed as unable to take a command, as a state rather than as something skipped. */
export function describeBlockReason(reason: SkipReason): string {
	return reason === 'offline' ? 'Offline' : skipWording[reason];
}

/** How long an offline host has to receive a command: the agent refuses one signed longer ago than this. */
const AGENT_COMMAND_WINDOW_MINUTES = 5;

export const OFFLINE_HOST_NOTE = `Offline. The host must receive the command within ${AGENT_COMMAND_WINDOW_MINUTES} minutes, or it will refuse it.`;

export const OFFLINE_HOST_ACCEPTED = `Accepted. The host is offline, and it will refuse the command unless it receives it within ${AGENT_COMMAND_WINDOW_MINUTES} minutes.`;

const reporterErrorWording: Record<string, string> = {
	credentials_rejected: 'Credentials rejected',
	tls_ca_changed: 'Cluster CA changed',
	state_copied: 'Reporter state was copied from another host; unenrol or re-enrol this host',
	api_unreachable: 'API server unreachable',
	namespace_terminating: 'The vord-system namespace is terminating'
};

/** Words an error code a reporter reported, or makes a readable label of a code it has no wording for. */
export function describeReporterError(code: string): string {
	const readable = code.replaceAll('_', ' ');

	return reporterErrorWording[code] ?? readable.charAt(0).toUpperCase() + readable.slice(1);
}

export function describeSendError(error: unknown): string {
	return error instanceof Error ? error.message : 'Failed to send the command';
}
