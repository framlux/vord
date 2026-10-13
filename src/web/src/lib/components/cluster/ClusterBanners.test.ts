// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { KubernetesClusterDetailDto } from '$lib/api/types';
import { formatDateTime } from '$lib/utils/format';
import ClusterBanners from './ClusterBanners.svelte';

function makeCluster(overrides: Partial<KubernetesClusterDetailDto> = {}): KubernetesClusterDetailDto {
	return {
		id: 12,
		name: 'Production',
		displayName: null,
		distro: 'k3s',
		nodeCount: 1,
		managedNodeCount: 1,
		unmanagedNodeCount: 0,
		kubeletVersions: ['v1.31.4+k3s1'],
		readyCount: 1,
		notReadyCount: 0,
		unknownCount: 0,
		reportingState: 'active',
		activeReporterMachineId: 7,
		activeReporterMachineName: 'cp-1',
		clusterVersion: 'v1.31.4+k3s1',
		reporterError: null,
		truncated: false,
		lastSnapshotReceivedAt: '2026-10-12T09:55:00Z',
		staleSince: null,
		firstSeenAt: '2026-10-01T00:00:00Z',
		lastSeenAt: '2026-10-12T09:55:00Z',
		caFingerprint: 'ab12cd34ef56',
		hasFingerprintMismatch: false,
		kubeSystemUid: 'uid',
		nodes: [],
		candidates: [],
		eligibleMachines: [],
		...overrides
	};
}

function renderBanners(
	cluster: KubernetesClusterDetailDto,
	options: { canAdmin?: boolean; busy?: boolean; onReenrol?: () => void; onUpdatePermissions?: () => void } = {}
) {
	return render(ClusterBanners, {
		props: {
			cluster,
			canAdmin: options.canAdmin ?? true,
			busy: options.busy ?? false,
			onReenrol: options.onReenrol ?? (() => {}),
			onUpdatePermissions: options.onUpdatePermissions ?? (() => {})
		}
	});
}

describe('ClusterBanners', () => {
	it('shows nothing for a healthy cluster', () => {
		renderBanners(makeCluster());

		expect(screen.queryByRole('alert')).not.toBeInTheDocument();
		expect(screen.queryByRole('button')).not.toBeInTheDocument();
	});

	describe('truncated', () => {
		it('says the node list is incomplete', () => {
			renderBanners(makeCluster({ truncated: true }));

			expect(screen.getByTestId('truncated-banner')).toHaveTextContent(
				'Only some of this cluster\'s nodes are listed, so the node table is incomplete.'
			);
		});

		it('is absent from a cluster whose snapshot listed every node', () => {
			renderBanners(makeCluster({ truncated: false }));

			expect(screen.queryByTestId('truncated-banner')).not.toBeInTheDocument();
		});
	});

	describe('stale', () => {
		it('says since when the details date', () => {
			const staleSince = '2026-10-12T06:00:00Z';
			renderBanners(makeCluster({ reportingState: 'stale', staleSince }));

			expect(screen.getByTestId('stale-banner')).toHaveTextContent(`Stale since ${formatDateTime(staleSince)}`);
		});

		it('is absent from a cluster with fresh details', () => {
			renderBanners(makeCluster({ staleSince: null }));

			expect(screen.queryByTestId('stale-banner')).not.toBeInTheDocument();
		});
	});

	describe('fingerprint mismatch', () => {
		it('warns that nodes report a different CA', () => {
			renderBanners(makeCluster({ hasFingerprintMismatch: true }));

			expect(screen.getByTestId('fingerprint-mismatch-banner')).toHaveTextContent(
				'Some nodes report a different cluster CA than this cluster. This is expected during a CA rotation.'
			);
		});

		it('is absent when every node agrees', () => {
			renderBanners(makeCluster({ hasFingerprintMismatch: false }));

			expect(screen.queryByTestId('fingerprint-mismatch-banner')).not.toBeInTheDocument();
		});
	});

	describe.each([
		['credentials_rejected', 'credentials-rejected-banner', 'The cluster rejected the reporter\'s credentials.'],
		['tls_ca_changed', 'tls-ca-changed-banner', 'The cluster\'s certificate authority changed'],
		['state_copied', 'state-copied-banner', 'copied from another host']
	])('%s', (code, testId, text) => {
		it('explains the failure', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: code }));

			expect(screen.getByTestId(testId)).toHaveTextContent(text);
		});

		it('offers to enrol again to a machine admin', async () => {
			const onReenrol = vi.fn();
			renderBanners(makeCluster({ reportingState: 'error', reporterError: code }), { onReenrol });

			await fireEvent.click(screen.getByRole('button', { name: 'Re-enrol' }));

			expect(onReenrol).toHaveBeenCalledTimes(1);
		});

		it('offers a viewer no action', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: code }), { canAdmin: false });

			expect(screen.getByTestId(testId)).toBeInTheDocument();
			expect(screen.queryByRole('button', { name: 'Re-enrol' })).not.toBeInTheDocument();
		});

		it('disables the action while a command is being sent', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: code }), { busy: true });

			expect(screen.getByRole('button', { name: 'Re-enrol' })).toBeDisabled();
		});
	});

	describe('role outdated', () => {
		it('explains that the reporter\'s permissions are old', () => {
			renderBanners(makeCluster({ reportingState: 'role_outdated' }));

			expect(screen.getByTestId('role-outdated-banner')).toHaveTextContent(
				'The reporter\'s permissions are older than this version of Vord needs'
			);
		});

		it('offers to update the permissions', async () => {
			const onUpdatePermissions = vi.fn();
			renderBanners(makeCluster({ reportingState: 'role_outdated' }), { onUpdatePermissions });

			await fireEvent.click(screen.getByRole('button', { name: 'Update permissions' }));

			expect(onUpdatePermissions).toHaveBeenCalledTimes(1);
		});

		it('offers a viewer no action', () => {
			renderBanners(makeCluster({ reportingState: 'role_outdated' }), { canAdmin: false });

			expect(screen.queryByRole('button', { name: 'Update permissions' })).not.toBeInTheDocument();
		});
	});

	describe('namespace terminating', () => {
		it('says reporting cannot continue until the namespace is gone, with no action', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: 'namespace_terminating' }));

			expect(screen.getByTestId('namespace-terminating-banner')).toHaveTextContent(
				'The vord-system namespace is being deleted'
			);
			expect(screen.queryByRole('button')).not.toBeInTheDocument();
		});
	});

	describe('other errors', () => {
		it('says the API server is unreachable, with no action', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: 'api_unreachable' }));

			expect(screen.getByTestId('api-unreachable-banner')).toHaveTextContent('cannot reach the cluster\'s API server');
			expect(screen.queryByRole('button')).not.toBeInTheDocument();
		});

		it('shows an error code it has no wording for rather than hiding it', () => {
			renderBanners(makeCluster({ reportingState: 'error', reporterError: 'disk_full' }));

			expect(screen.getByTestId('reporter-error-banner')).toHaveTextContent('Disk full');
		});
	});

	it('shows every banner that applies at once', () => {
		renderBanners(
			makeCluster({
				reportingState: 'error',
				reporterError: 'credentials_rejected',
				truncated: true,
				staleSince: '2026-10-12T06:00:00Z',
				hasFingerprintMismatch: true
			})
		);

		expect(screen.getAllByRole('alert')).toHaveLength(4);
	});
});
