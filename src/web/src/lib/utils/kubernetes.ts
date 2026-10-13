// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type {
	KubernetesNodeConditionDto,
	KubernetesNodeDto,
	KubernetesReportingState
} from '$lib/api/types';

// The label for a Kubernetes distribution value as the server reports it. It spells the five
// distributions Vord detects the way their projects do, and calls anything else plain Kubernetes,
// matching the label the server puts in a cluster's default name.
export function distroLabel(distro: string): string {
	switch (distro) {
		case 'k3s':
			return 'k3s';
		case 'rke2':
			return 'RKE2';
		case 'kubeadm':
			return 'kubeadm';
		case 'k0s':
			return 'k0s';
		case 'microk8s':
			return 'MicroK8s';
		default:
			return 'Kubernetes';
	}
}

export type BadgeTone = 'ok' | 'bad' | 'warn' | 'neutral';

export interface Badge {
	label: string;
	tone: BadgeTone;
}

// The badge for a node's Ready condition, or null when no cluster snapshot has listed the node. An
// Unknown condition is the node controller having lost contact, which is not the same as the node
// reporting itself unready.
export function readyBadge(status: string | null): Badge | null {
	switch (status) {
		case null:
			return null;
		case 'True':
			return { label: 'Ready', tone: 'ok' };
		case 'False':
			return { label: 'Not ready', tone: 'bad' };
		default:
			return { label: 'Unknown', tone: 'warn' };
	}
}

const conditionWording: Record<string, string> = {
	MemoryPressure: 'Memory pressure',
	DiskPressure: 'Disk pressure',
	PIDPressure: 'PID pressure',
	NetworkUnavailable: 'Network unavailable'
};

export function conditionLabel(condition: KubernetesNodeConditionDto): string {
	const label = conditionWording[condition.type] ?? condition.type;

	return condition.status === 'Unknown' ? `${label} (unknown)` : label;
}

// A node's machine can say it runs a control plane before any cluster snapshot has listed the node's
// roles, so the role is added when the reporter's list lacks it. master is the older name for it.
export function nodeRoles(node: Pick<KubernetesNodeDto, 'roles' | 'controlPlane'>): string[] {
	const roles = [...new Set(node.roles)];
	if (node.controlPlane && roles.includes('control-plane') === false && roles.includes('master') === false) {
		roles.unshift('control-plane');
	}

	return roles;
}

export function podsLabel(node: Pick<KubernetesNodeDto, 'podCount' | 'podCapacity'>): string {
	if (node.podCount === null) {
		return '---';
	}

	return node.podCapacity === null ? `${node.podCount}` : `${node.podCount}/${node.podCapacity}`;
}

const reportingWording: Record<KubernetesReportingState, Badge> = {
	off: { label: 'Off', tone: 'neutral' },
	active: { label: 'Active', tone: 'ok' },
	stale: { label: 'Stale', tone: 'warn' },
	error: { label: 'Error', tone: 'bad' },
	role_outdated: { label: 'Role outdated', tone: 'warn' }
};

// The badge for a cluster's reporting state. A state this build has no wording for is shown as the
// server sent it rather than hidden.
export function reportingBadge(state: KubernetesReportingState, activeReporterName: string | null): Badge {
	const badge = reportingWording[state] ?? { label: state, tone: 'neutral' };

	return state === 'active' && activeReporterName !== null ? { ...badge, label: `${badge.label} (${activeReporterName})` } : badge;
}
