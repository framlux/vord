// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { UserAccountRole } from '$lib/api/types';
import type { KubernetesClusterDetailDto, KubernetesNodeDto, UserDto } from '$lib/api/types';

import ClusterPage from './+page.svelte';
import type { ActionData } from './$types';

function makeNode(overrides: Partial<KubernetesNodeDto> = {}): KubernetesNodeDto {
	return {
		id: 1,
		nodeName: 'cp-1',
		controlPlane: true,
		kubeletVersion: 'v1.31.4+k3s1',
		machineId: 7,
		machineName: 'web-01',
		fingerprintMismatch: false,
		lastLocalReportAt: '2026-10-11T11:30:00Z',
		...overrides
	};
}

function makeCluster(overrides: Partial<KubernetesClusterDetailDto> = {}): KubernetesClusterDetailDto {
	return {
		id: 12,
		name: 'k3s cp-1',
		displayName: null,
		distro: 'k3s',
		nodeCount: 1,
		managedNodeCount: 1,
		unmanagedNodeCount: 0,
		kubeletVersions: ['v1.31.4+k3s1'],
		reportingState: 'identity_only',
		firstSeenAt: '2026-10-01T00:00:00Z',
		lastSeenAt: '2026-10-11T11:30:00Z',
		caFingerprint: 'ab12cd34ef56',
		hasFingerprintMismatch: false,
		nodes: [makeNode()],
		...overrides
	};
}

function makeUser(role: UserAccountRole = UserAccountRole.TenantAdmin): UserDto {
	return {
		id: 1,
		name: 'Jonathan Miller',
		email: 'jonathan@acme.co',
		avatar: '',
		isGlobalAdmin: false,
		uniqueId: 'uid-1',
		needsOnboarding: false,
		tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: String(role) }],
		activeTenantId: 1,
		deployment: { selfHosted: false }
	};
}

function renderPage(
	cluster: KubernetesClusterDetailDto,
	options: { role?: UserAccountRole; form?: ActionData } = {}
) {
	return render(ClusterPage, {
		props: {
			data: { cluster, user: makeUser(options.role), subscription: null },
			form: options.form ?? null
		}
	});
}

const MISMATCH_BANNER =
	'Some nodes report a different cluster CA than this cluster. This is expected during a CA rotation.';

describe('cluster detail', () => {
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ['Date'] });
		vi.setSystemTime(new Date('2026-10-11T12:00:00Z'));
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('headlines the page with the cluster name', () => {
		renderPage(makeCluster({ name: 'Production' }));

		expect(screen.getByRole('heading', { level: 1, name: 'Production' })).toBeInTheDocument();
	});

	it('links back to the clusters list', () => {
		renderPage(makeCluster());

		expect(screen.getByRole('link', { name: /Clusters/ })).toHaveAttribute('href', '/clusters');
	});

	describe('node table', () => {
		it('labels the columns', () => {
			renderPage(makeCluster());

			const headers = screen.getAllByRole('columnheader').map((h) => h.textContent?.trim());

			expect(headers).toEqual(['Node', 'Role', 'Kubelet', 'Machine', 'Cluster CA', 'Last report']);
		});

		it('links a managed node to its machine by name', () => {
			renderPage(makeCluster({ nodes: [makeNode({ machineId: 7, machineName: 'web-01' })] }));

			const row = screen.getByRole('row', { name: /cp-1/ });

			expect(within(row).getByRole('link', { name: 'web-01' })).toHaveAttribute('href', '/machines/7');
			expect(within(row).queryByText('Not managed by Vord')).not.toBeInTheDocument();
		});

		it('says an unmanaged node is not managed by Vord, with no link', () => {
			renderPage(
				makeCluster({ nodes: [makeNode({ nodeName: 'worker-9', machineId: null, machineName: null })] })
			);

			const row = screen.getByRole('row', { name: /worker-9/ });

			expect(within(row).getByText('Not managed by Vord')).toBeInTheDocument();
			expect(within(row).queryByRole('link')).not.toBeInTheDocument();
		});

		it('shows managed and unmanaged nodes side by side', () => {
			renderPage(
				makeCluster({
					nodes: [
						makeNode({ id: 1, nodeName: 'cp-1', machineId: 7, machineName: 'web-01' }),
						makeNode({ id: 2, nodeName: 'worker-9', controlPlane: false, machineId: null, machineName: null })
					]
				})
			);

			expect(screen.getAllByRole('row')).toHaveLength(3);
			expect(screen.getByRole('link', { name: 'web-01' })).toBeInTheDocument();
			expect(screen.getByText('Not managed by Vord')).toBeInTheDocument();
		});

		it('marks a control plane node with a role chip and leaves a worker unmarked', () => {
			renderPage(
				makeCluster({
					nodes: [
						makeNode({ id: 1, nodeName: 'cp-1', controlPlane: true }),
						makeNode({ id: 2, nodeName: 'worker-9', controlPlane: false })
					]
				})
			);

			expect(within(screen.getByRole('row', { name: /cp-1/ })).getByText('control plane')).toBeInTheDocument();
			expect(within(screen.getByRole('row', { name: /worker-9/ })).queryByText('control plane')).not.toBeInTheDocument();
		});

		it('shows the kubelet version and a placeholder when a node has none', () => {
			renderPage(
				makeCluster({
					nodes: [
						makeNode({ id: 1, nodeName: 'cp-1', kubeletVersion: 'v1.31.4+k3s1' }),
						makeNode({ id: 2, nodeName: 'worker-9', kubeletVersion: null })
					]
				})
			);

			expect(within(screen.getByRole('row', { name: /cp-1/ })).getByText('v1.31.4+k3s1')).toBeInTheDocument();
			expect(within(screen.getByRole('row', { name: /worker-9/ })).getByText('---')).toBeInTheDocument();
		});

		it('flags a node whose cluster CA differs from the cluster', () => {
			renderPage(
				makeCluster({
					hasFingerprintMismatch: true,
					nodes: [
						makeNode({ id: 1, nodeName: 'cp-1', fingerprintMismatch: true }),
						makeNode({ id: 2, nodeName: 'cp-2', fingerprintMismatch: false })
					]
				})
			);

			expect(within(screen.getByRole('row', { name: /cp-1/ })).getByText('CA mismatch')).toBeInTheDocument();
			expect(within(screen.getByRole('row', { name: /cp-2/ })).queryByText('CA mismatch')).not.toBeInTheDocument();
		});

		it('shows when the node last reported, relative to now, and a placeholder if it never has', () => {
			renderPage(
				makeCluster({
					nodes: [
						makeNode({ id: 1, nodeName: 'cp-1', lastLocalReportAt: '2026-10-11T11:30:00Z' }),
						makeNode({ id: 2, nodeName: 'worker-9', lastLocalReportAt: null })
					]
				})
			);

			expect(within(screen.getByRole('row', { name: /cp-1/ })).getByText('30m ago')).toBeInTheDocument();
			expect(within(screen.getByRole('row', { name: /worker-9/ })).getAllByRole('cell').at(-1)).toHaveTextContent('---');
		});
	});

	describe('fingerprint mismatch banner', () => {
		it('warns that nodes report a different CA when the cluster has a mismatch', () => {
			renderPage(makeCluster({ hasFingerprintMismatch: true }));

			expect(screen.getByText(MISMATCH_BANNER)).toBeInTheDocument();
		});

		it('says nothing when every node agrees with the cluster', () => {
			renderPage(makeCluster({ hasFingerprintMismatch: false }));

			expect(screen.queryByText(MISMATCH_BANNER)).not.toBeInTheDocument();
		});
	});

	describe('rename form', () => {
		it('is offered to a machine admin', () => {
			renderPage(makeCluster(), { role: UserAccountRole.MachineAdmin });

			expect(screen.getByLabelText('Cluster name')).toBeInTheDocument();
			expect(screen.getByRole('button', { name: 'Rename' })).toBeInTheDocument();
		});

		it('is offered to a tenant admin', () => {
			renderPage(makeCluster(), { role: UserAccountRole.TenantAdmin });

			expect(screen.getByRole('button', { name: 'Rename' })).toBeInTheDocument();
		});

		it('is hidden from a viewer, who cannot rename', () => {
			renderPage(makeCluster(), { role: UserAccountRole.Viewer });

			expect(screen.queryByLabelText('Cluster name')).not.toBeInTheDocument();
			expect(screen.queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument();
		});

		it('posts to the rename action', () => {
			renderPage(makeCluster());

			const form = screen.getByLabelText('Cluster name').closest('form');

			expect(form).toHaveAttribute('method', 'POST');
			expect(form).toHaveAttribute('action', '?/rename');
		});

		it('starts empty while the cluster uses its default name', () => {
			renderPage(makeCluster({ displayName: null }));

			expect(screen.getByLabelText('Cluster name')).toHaveValue('');
		});

		it('starts with the chosen name once the cluster has one', () => {
			renderPage(makeCluster({ name: 'Production', displayName: 'Production' }));

			expect(screen.getByLabelText('Cluster name')).toHaveValue('Production');
		});

		it('says an empty name resets to the default', () => {
			renderPage(makeCluster());

			expect(screen.getByText('Leave empty to use the default name.')).toBeInTheDocument();
		});

		it('shows the message of a failed rename and keeps what was typed', () => {
			renderPage(makeCluster(), {
				form: { message: 'Display name must be 100 characters or fewer.', displayName: 'a very long name' }
			});

			expect(screen.getByRole('alert')).toHaveTextContent('Display name must be 100 characters or fewer.');
			expect(screen.getByLabelText('Cluster name')).toHaveValue('a very long name');
		});

		it('shows no error after a successful rename', () => {
			renderPage(makeCluster(), { form: { success: true } });

			expect(screen.queryByRole('alert')).not.toBeInTheDocument();
		});
	});
});
