// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { conditionLabel, distroLabel, nodeRoles, podsLabel, readyBadge, reportingBadge } from './kubernetes';

describe('distroLabel', () => {
	it.each([
		['k3s', 'k3s'],
		['rke2', 'RKE2'],
		['kubeadm', 'kubeadm'],
		['k0s', 'k0s'],
		['microk8s', 'MicroK8s']
	])('spells %s the way its project does', (distro, label) => {
		expect(distroLabel(distro)).toBe(label);
	});

	it('calls an unknown or unrecognised distribution plain Kubernetes', () => {
		expect(distroLabel('unknown')).toBe('Kubernetes');
		expect(distroLabel('something-new')).toBe('Kubernetes');
		expect(distroLabel('')).toBe('Kubernetes');
	});
});

describe('readyBadge', () => {
	it('calls a True Ready condition ready', () => {
		expect(readyBadge('True')).toEqual({ label: 'Ready', tone: 'ok' });
	});

	it('calls a False Ready condition not ready', () => {
		expect(readyBadge('False')).toEqual({ label: 'Not ready', tone: 'bad' });
	});

	it('calls an Unknown Ready condition unknown, which is not the same as not ready', () => {
		expect(readyBadge('Unknown')).toEqual({ label: 'Unknown', tone: 'warn' });
	});

	it('has no badge for a node no snapshot has listed', () => {
		expect(readyBadge(null)).toBeNull();
	});
});

describe('conditionLabel', () => {
	it.each([
		['MemoryPressure', 'Memory pressure'],
		['DiskPressure', 'Disk pressure'],
		['PIDPressure', 'PID pressure'],
		['NetworkUnavailable', 'Network unavailable']
	])('words %s', (type, label) => {
		expect(conditionLabel({ type, status: 'True', lastTransitionTime: '' })).toBe(label);
	});

	it('marks a condition whose status is unknown', () => {
		expect(conditionLabel({ type: 'DiskPressure', status: 'Unknown', lastTransitionTime: '' })).toBe(
			'Disk pressure (unknown)'
		);
	});

	it('shows a condition it has no wording for as the cluster named it', () => {
		expect(conditionLabel({ type: 'KernelDeadlock', status: 'True', lastTransitionTime: '' })).toBe('KernelDeadlock');
	});
});

describe('nodeRoles', () => {
	it('lists the roles the reporter gave, once each', () => {
		expect(nodeRoles({ roles: ['etcd', 'control-plane', 'etcd'], controlPlane: true })).toEqual(['etcd', 'control-plane']);
	});

	it('adds control-plane for a node its machine reported as a control plane but the reporter has not listed', () => {
		expect(nodeRoles({ roles: [], controlPlane: true })).toEqual(['control-plane']);
	});

	it('does not add it when the reporter already lists master, the older name for the role', () => {
		expect(nodeRoles({ roles: ['master'], controlPlane: true })).toEqual(['master']);
	});

	it('has no roles for a worker the reporter lists none for', () => {
		expect(nodeRoles({ roles: [], controlPlane: false })).toEqual([]);
	});
});

describe('podsLabel', () => {
	it('shows pods against capacity', () => {
		expect(podsLabel({ podCount: 12, podCapacity: 110 })).toBe('12/110');
	});

	it('shows a placeholder for a node whose pods were not counted', () => {
		expect(podsLabel({ podCount: null, podCapacity: 110 })).toBe('---');
		expect(podsLabel({ podCount: null, podCapacity: null })).toBe('---');
	});

	it('shows zero pods as zero', () => {
		expect(podsLabel({ podCount: 0, podCapacity: 110 })).toBe('0/110');
	});

	it('shows the count alone when the capacity is unknown', () => {
		expect(podsLabel({ podCount: 7, podCapacity: null })).toBe('7');
	});
});

describe('reportingBadge', () => {
	it.each([
		['off', 'Off'],
		['active', 'Active'],
		['stale', 'Stale'],
		['error', 'Error'],
		['role_outdated', 'Role outdated']
	] as const)('words %s', (state, label) => {
		expect(reportingBadge(state, null).label).toBe(label);
	});

	it('names the holder of an active cluster', () => {
		expect(reportingBadge('active', 'cp-1').label).toBe('Active (cp-1)');
	});

	it('names no holder for any other state', () => {
		expect(reportingBadge('stale', 'cp-1').label).toBe('Stale');
	});

	it('shows a state it has no wording for as the server sent it', () => {
		expect(reportingBadge('something_new' as never, null).label).toBe('something_new');
	});
});
