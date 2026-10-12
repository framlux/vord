// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { KubernetesClusterSummaryDto, UserDto } from '$lib/api/types';

import ClustersPage from './+page.svelte';

function makeCluster(overrides: Partial<KubernetesClusterSummaryDto> = {}): KubernetesClusterSummaryDto {
	return {
		id: 4,
		name: 'k3s cp-1',
		displayName: null,
		distro: 'k3s',
		nodeCount: 3,
		managedNodeCount: 2,
		unmanagedNodeCount: 1,
		kubeletVersions: ['v1.31.4+k3s1'],
		readyCount: 0,
		notReadyCount: 0,
		unknownCount: 3,
		reportingState: 'off',
		activeReporterMachineId: null,
		activeReporterMachineName: null,
		clusterVersion: null,
		reporterError: null,
		truncated: false,
		lastSnapshotReceivedAt: null,
		staleSince: null,
		firstSeenAt: '2026-10-01T00:00:00Z',
		lastSeenAt: '2026-10-11T11:30:00Z',
		...overrides
	};
}

function makeUser(): UserDto {
	return {
		id: 1,
		name: 'Jonathan Miller',
		email: 'jonathan@acme.co',
		avatar: '',
		isGlobalAdmin: false,
		uniqueId: 'uid-1',
		needsOnboarding: false,
		tenants: [{ tenantId: 1, tenantName: 'Acme Corp', role: '1' }],
		activeTenantId: 1,
		deployment: { selfHosted: false }
	};
}

function makeData(clusters: KubernetesClusterSummaryDto[]) {
	return { clusters, user: makeUser(), subscription: null };
}

describe('clusters list', () => {
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ['Date'] });
		vi.setSystemTime(new Date('2026-10-11T12:00:00Z'));
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it('titles the page Clusters', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster()]) } });

		expect(screen.getByRole('heading', { level: 1, name: 'Clusters' })).toBeInTheDocument();
	});

	it('explains where clusters come from when the tenant has none', () => {
		render(ClustersPage, { props: { data: makeData([]) } });

		expect(screen.getByText('No Kubernetes clusters yet')).toBeInTheDocument();
		expect(
			screen.getByText('Clusters appear automatically when a Vord agent runs on a Kubernetes node.')
		).toBeInTheDocument();
		expect(screen.queryByRole('table')).not.toBeInTheDocument();
	});

	it('lists a cluster with its name linking to the cluster page', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster({ id: 4, name: 'Production' })]) } });

		expect(screen.getByRole('link', { name: 'Production' })).toHaveAttribute('href', '/clusters/4');
		expect(screen.queryByText('No Kubernetes clusters yet')).not.toBeInTheDocument();
	});

	it('shows the distro label, node counts, kubelet versions, reporting badge and last seen time', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster({ distro: 'microk8s', kubeletVersions: ['v1.30.1', 'v1.31.0'] })]) } });

		const cells = within(screen.getByRole('row', { name: /k3s cp-1/ })).getAllByRole('cell');

		expect(cells.map((c) => c.textContent?.trim())).toEqual([
			'k3s cp-1',
			'MicroK8s',
			'3',
			'2',
			'1',
			'v1.30.1, v1.31.0',
			'Off',
			'30m ago'
		]);
	});

	it.each([
		['off', 'Off'],
		['active', 'Active'],
		['stale', 'Stale'],
		['error', 'Error'],
		['role_outdated', 'Role outdated']
	] as const)('shows the %s reporting state as %s', (reportingState, label) => {
		render(ClustersPage, { props: { data: makeData([makeCluster({ reportingState })]) } });

		const cells = within(screen.getByRole('row', { name: /k3s cp-1/ })).getAllByRole('cell');

		expect(cells[6]).toHaveTextContent(new RegExp(`^${label}$`));
	});

	it('names the machine that reports an active cluster', () => {
		render(ClustersPage, {
			props: {
				data: makeData([
					makeCluster({ reportingState: 'active', activeReporterMachineId: 7, activeReporterMachineName: 'cp-1' })
				])
			}
		});

		const cells = within(screen.getByRole('row', { name: /k3s cp-1/ })).getAllByRole('cell');

		expect(cells[6]).toHaveTextContent('Active (cp-1)');
	});

	it('no longer calls any cluster identity only', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster()]) } });

		expect(screen.queryByText(/identity only/i)).not.toBeInTheDocument();
	});

	it('labels the columns, with managed and unmanaged nodes counted apart', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster()]) } });

		const headers = screen.getAllByRole('columnheader').map((h) => h.textContent?.trim());

		expect(headers).toEqual([
			'Name',
			'Distro',
			'Nodes',
			'Managed',
			'Unmanaged',
			'Kubelet',
			'Reporting',
			'Last seen'
		]);
	});

	it('shows a placeholder for a cluster whose nodes report no kubelet version', () => {
		render(ClustersPage, { props: { data: makeData([makeCluster({ kubeletVersions: [] })]) } });

		const cells = within(screen.getByRole('row', { name: /k3s cp-1/ })).getAllByRole('cell');

		expect(cells[5]).toHaveTextContent('---');
	});

	it('lists every cluster it is given', () => {
		render(ClustersPage, {
			props: {
				data: makeData([
					makeCluster({ id: 1, name: 'alpha' }),
					makeCluster({ id: 2, name: 'beta', distro: 'rke2' })
				])
			}
		});

		expect(screen.getByRole('link', { name: 'alpha' })).toHaveAttribute('href', '/clusters/1');
		expect(screen.getByRole('link', { name: 'beta' })).toHaveAttribute('href', '/clusters/2');
		expect(screen.getByText('RKE2')).toBeInTheDocument();
	});
});
