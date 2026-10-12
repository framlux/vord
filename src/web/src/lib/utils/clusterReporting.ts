// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { ApiError } from '$lib/api/client';
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

/**
 * Decides whether a machine can be sent a command and with which of the user's keys. The order of
 * the reasons follows the server's: the key is checked before the pause, and the pause before the
 * capability. A machine whose capabilities could not be read (null) is allowed: unknown is not disabled.
 */
export function assessMachine(machine: KubernetesEligibleMachineDto, keys: UsableSigningKey[]): MachineAssessment {
	const key = keys.find((k) => machine.authorizedSigningKeyIds.includes(k.serverKeyId));
	if (key === undefined) {
		return { skip: 'key_not_authorised' };
	}
	if (machine.isPaused) {
		return { skip: 'paused' };
	}
	if (machine.remoteCommandsEnabled === false) {
		return { skip: 'remote_commands_disabled' };
	}

	return { key };
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

/** Enrols every eligible machine that is not enrolled yet and can be sent the command. */
export function planEnable(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	for (const machine of context.eligibleMachines) {
		if (machine.enrolled || isCandidate(context, machine.machineId)) {
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
	const assessment = assessMachine(machine, keys);
	if ('key' in assessment) {
		plan.commands.push(command(machine.machineId, machine.machineName, AGENT_ENROL, assessment.key, machine.online));
	} else {
		plan.skipped.push({ machineId: machine.machineId, machineName: machine.machineName, reason: assessment.skip });
	}
}

/** Why reporting cannot be disabled at all right now, or null when it can be tried. */
export function disableBlock(context: ReportingContext): string | null {
	if (context.eligibleMachines.some((m) => m.online)) {
		return null;
	}

	return 'Reporting can only be disabled through a control-plane machine with an admin credential, and none is online right now.';
}

/**
 * Uninstalls through one online eligible machine, preferring the holder and then a candidate, and
 * unenrols every other online candidate. Without a machine to uninstall through nothing is sent: an
 * unenrol alone would drop the hosts' tokens and leave the cluster objects behind.
 */
export function planDisable(context: ReportingContext, keys: UsableSigningKey[]): ReportingPlan {
	const plan = emptyPlan();
	const holder = holderId(context);

	const viable = context.eligibleMachines
		.filter((m) => m.online)
		.map((machine) => ({ machine, assessment: assessMachine(machine, keys) }))
		.filter((entry): entry is { machine: KubernetesEligibleMachineDto; assessment: { key: UsableSigningKey } } =>
			'key' in entry.assessment
		);
	const rank = (machineId: number): number => (machineId === holder ? 0 : isCandidate(context, machineId) ? 1 : 2);
	viable.sort((a, b) => rank(a.machine.machineId) - rank(b.machine.machineId));
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
		described !== undefined ? assessMachine(described, keys) : keys.length > 0 ? { key: keys[0] } : { skip: 'key_not_authorised' };

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

/** Enrols once, through the holder if it can be sent the command and otherwise through another eligible candidate. */
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
		(candidate.machineId === holder ? 0 : 2) + (candidate.online ? 0 : 1);
	considered.sort((a, b) => rank(a.candidate) - rank(b.candidate));

	for (const { candidate, machine } of considered) {
		const assessment = assessMachine(machine, keys);
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

const agentCodeWording: Record<string, string> = {
	credentials_rejected: 'Credentials rejected',
	tls_ca_changed: 'Cluster CA changed',
	api_unreachable: 'API server unreachable',
	namespace_terminating: 'The vord-system namespace is terminating'
};

export interface AgentMessage {
	code: string;
	label: string;
	detail: string | null;
}

/**
 * Words a message the agent reported, which is a bare code or "code: detail". Only the first colon
 * splits, since the detail may hold addresses and other colons of its own.
 */
export function describeAgentMessage(message: string): AgentMessage {
	const colon = message.indexOf(':');
	const code = (colon === -1 ? message : message.slice(0, colon)).trim();
	const detail = colon === -1 ? '' : message.slice(colon + 1).trim();
	const readable = code.replaceAll('_', ' ');

	return {
		code,
		label: agentCodeWording[code] ?? readable.charAt(0).toUpperCase() + readable.slice(1),
		detail: detail === '' ? null : detail
	};
}

// The API client reports every 403 as a bare "Forbidden" without reading the response body, so the
// server's Team-tier message never reaches this code. A machine admin reaching the send has had the
// key checked against the machine already, so the subscription is what the refusal most likely means.
const forbiddenWording =
	'Forbidden. Remote commands require a Team subscription and a signing key authorised on this host.';

export function describeSendError(error: unknown): string {
	if (error instanceof ApiError && error.status === 403 && error.message === 'Forbidden') {
		return forbiddenWording;
	}

	return error instanceof Error ? error.message : 'Failed to send the command';
}
