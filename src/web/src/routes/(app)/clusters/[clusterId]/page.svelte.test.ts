// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, within, fireEvent, waitFor } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { UserAccountRole } from '$lib/api/types';
import type {
	KubernetesCandidateDto,
	KubernetesClusterDetailDto,
	KubernetesEligibleMachineDto,
	KubernetesNodeDto,
	SendCommandRequest,
	SigningKeyDto,
	UserDto
} from '$lib/api/types';
import { formatDateTime } from '$lib/utils/format';

const { api, getLocalKeys, signPayload, invalidateAll } = vi.hoisted(() => ({
	api: { sendCommand: vi.fn(), getSigningKeys: vi.fn() },
	getLocalKeys: vi.fn(),
	signPayload: vi.fn(),
	invalidateAll: vi.fn()
}));

vi.mock('$app/navigation', () => ({ invalidateAll }));

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
		}
	};
});

vi.mock('$lib/crypto/signing', async (importOriginal) => ({
	...(await importOriginal<typeof import('$lib/crypto/signing')>()),
	getLocalKeys,
	signPayload
}));

import { ApiError } from '$lib/api/client';
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
		roles: [],
		readyStatus: null,
		readySince: null,
		conditions: [],
		unschedulable: false,
		unschedulableSince: null,
		containerRuntimeVersion: null,
		podCount: null,
		podCapacity: null,
		lastReporterSeenAt: null,
		kubeletSkew: false,
		...overrides
	};
}

function makeCandidate(overrides: Partial<KubernetesCandidateDto> = {}): KubernetesCandidateDto {
	return {
		machineId: 7,
		machineName: 'web-01',
		isHolder: false,
		reporterError: null,
		lastReportAt: '2026-10-11T11:45:00Z',
		online: true,
		...overrides
	};
}

function makeEligible(overrides: Partial<KubernetesEligibleMachineDto> = {}): KubernetesEligibleMachineDto {
	return {
		machineId: 7,
		machineName: 'web-01',
		online: true,
		isPaused: false,
		remoteCommandsEnabled: true,
		enrolled: false,
		authorizedSigningKeyIds: [41],
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
		readyCount: 0,
		notReadyCount: 0,
		unknownCount: 1,
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
		caFingerprint: 'ab12cd34ef56',
		hasFingerprintMismatch: false,
		kubeSystemUid: null,
		nodes: [makeNode()],
		candidates: [],
		eligibleMachines: [],
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

const SERVER_KEY: SigningKeyDto = {
	id: 41,
	label: 'Laptop',
	publicKey: 'PUBLIC-A',
	fingerprint: 'fp-a',
	createdAt: '2026-10-01T00:00:00Z',
	revokedAt: null
};

function useSigningKey() {
	getLocalKeys.mockResolvedValue([
		{ id: 3, label: 'Laptop', publicKeyBase64: 'PUBLIC-A', createdAt: '2026-10-01T00:00:00Z' }
	]);
	api.getSigningKeys.mockResolvedValue({ keys: [SERVER_KEY], activeCount: 1, maxKeys: 5 });
}

function sentCommands(): SendCommandRequest[] {
	return api.sendCommand.mock.calls.map((call) => call[0] as SendCommandRequest);
}

function sentSummary(): Array<[number, string]> {
	return sentCommands().map((c) => [c.machineId, c.commandType]);
}

const ENABLE_TEXT =
	"This creates a read-only ServiceAccount in your cluster, using each control-plane node's admin credentials. " +
	'Each enrolled node stores a long-lived token that can read node details, the namespace list, and full pod ' +
	'specifications, including environment-variable values. It cannot read Secrets or ConfigMaps. Anyone who ' +
	'obtains the token, for example from a backup of /var/lib/vord-agent, gets the same access. Disabling ' +
	'reporting revokes every token.';

describe('cluster detail', () => {
	beforeEach(() => {
		vi.useFakeTimers({ toFake: ['Date'] });
		vi.setSystemTime(new Date('2026-10-11T12:00:00Z'));
		api.sendCommand.mockReset();
		api.sendCommand.mockResolvedValue({});
		api.getSigningKeys.mockReset();
		getLocalKeys.mockReset();
		signPayload.mockReset();
		signPayload.mockResolvedValue('SIGNATURE');
		invalidateAll.mockReset();
		useSigningKey();
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
		it('says no nodes have reported yet, instead of an empty table, when the cluster has none', () => {
			renderPage(makeCluster({ nodes: [], nodeCount: 0, managedNodeCount: 0 }));

			expect(screen.getByText('No nodes reported yet')).toBeInTheDocument();
			expect(screen.queryByRole('table')).not.toBeInTheDocument();
			expect(screen.queryByRole('columnheader')).not.toBeInTheDocument();
		});

		it('shows the table, not the empty message, once a node has reported', () => {
			renderPage(makeCluster());

			expect(screen.queryByText('No nodes reported yet')).not.toBeInTheDocument();
			expect(screen.getByRole('table')).toBeInTheDocument();
		});

		it('labels the columns', () => {
			renderPage(makeCluster());

			const headers = screen.getAllByRole('columnheader').map((h) => h.textContent?.trim());

			expect(headers).toEqual([
				'Node',
				'Roles',
				'Status',
				'Kubelet',
				'Pods',
				'Machine',
				'Cluster CA',
				'Last report'
			]);
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

			expect(within(screen.getByRole('row', { name: /cp-1/ })).getByText('control-plane')).toBeInTheDocument();
			expect(within(screen.getByRole('row', { name: /worker-9/ })).queryByText('control-plane')).not.toBeInTheDocument();
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
			expect(within(screen.getByRole('row', { name: /worker-9/ })).getAllByRole('cell')[3]).toHaveTextContent('---');
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

	describe('node health', () => {
		function rowOf(name: RegExp) {
			return within(screen.getByRole('row', { name }));
		}

		it('shows the roles the reporter lists as chips', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', roles: ['control-plane', 'etcd'] })] }));

			expect(rowOf(/cp-1/).getByText('control-plane')).toBeInTheDocument();
			expect(rowOf(/cp-1/).getByText('etcd')).toBeInTheDocument();
		});

		it('shows a Ready badge for a ready node', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', readyStatus: 'True' })] }));

			expect(rowOf(/cp-1/).getByText('Ready')).toBeInTheDocument();
		});

		it('shows Not ready for a node whose Ready condition is False', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', readyStatus: 'False' })] }));

			expect(rowOf(/cp-1/).getByText('Not ready')).toBeInTheDocument();
		});

		it('shows Unknown for a node whose Ready condition is Unknown, not Not ready', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', readyStatus: 'Unknown' })] }));

			expect(rowOf(/cp-1/).getByText('Unknown')).toBeInTheDocument();
			expect(rowOf(/cp-1/).queryByText('Not ready')).not.toBeInTheDocument();
		});

		it('shows no Ready badge for a node no snapshot has listed', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', readyStatus: null })] }));

			expect(rowOf(/cp-1/).queryByText('Ready')).not.toBeInTheDocument();
			expect(rowOf(/cp-1/).queryByText('Not ready')).not.toBeInTheDocument();
		});

		it('flags the pressure conditions a node reports', () => {
			renderPage(
				makeCluster({
					nodes: [
						makeNode({
							nodeName: 'cp-1',
							readyStatus: 'True',
							conditions: [
								{ type: 'MemoryPressure', status: 'True', lastTransitionTime: '2026-10-11T10:00:00Z' },
								{ type: 'DiskPressure', status: 'Unknown', lastTransitionTime: '2026-10-11T10:00:00Z' }
							]
						})
					]
				})
			);

			expect(rowOf(/cp-1/).getByText('Memory pressure')).toBeInTheDocument();
			expect(rowOf(/cp-1/).getByText('Disk pressure (unknown)')).toBeInTheDocument();
		});

		it('says since when a node has been cordoned', () => {
			const since = '2026-10-11T08:00:00Z';
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', unschedulable: true, unschedulableSince: since })] }));

			expect(rowOf(/cp-1/).getByText(`Cordoned since ${formatDateTime(since)}`)).toBeInTheDocument();
		});

		it('says a node is cordoned even when it does not know since when', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', unschedulable: true, unschedulableSince: null })] }));

			expect(rowOf(/cp-1/).getByText('Cordoned')).toBeInTheDocument();
		});

		it('says nothing about cordoning for a schedulable node', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', unschedulable: false })] }));

			expect(rowOf(/cp-1/).queryByText(/Cordoned/)).not.toBeInTheDocument();
		});

		it('warns when the kubelet is out of step with the cluster', () => {
			renderPage(
				makeCluster({
					clusterVersion: 'v1.31.4+k3s1',
					nodes: [makeNode({ nodeName: 'cp-1', kubeletVersion: 'v1.26.0', kubeletSkew: true })]
				})
			);

			const warning = rowOf(/cp-1/).getByText('Version skew');

			expect(warning).toBeInTheDocument();
			expect(warning.closest('[title]')).toHaveAttribute('title', expect.stringContaining('v1.31.4+k3s1'));
		});

		it('does not warn about a kubelet that is in step', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', kubeletSkew: false })] }));

			expect(rowOf(/cp-1/).queryByText('Version skew')).not.toBeInTheDocument();
		});

		it('shows pods as count over capacity', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', podCount: 12, podCapacity: 110 })] }));

			expect(rowOf(/cp-1/).getByText('12/110')).toBeInTheDocument();
		});

		it('shows a placeholder for pods that were not counted', () => {
			renderPage(makeCluster({ nodes: [makeNode({ nodeName: 'cp-1', podCount: null, podCapacity: null })] }));

			expect(rowOf(/cp-1/).getAllByRole('cell')[4]).toHaveTextContent('---');
		});
	});

	describe('reporting status', () => {
		it('shows the reporting state and the machine that holds the reporter', () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					activeReporterMachineId: 7,
					activeReporterMachineName: 'web-01',
					lastSnapshotReceivedAt: '2026-10-11T11:55:00Z'
				})
			);

			const status = screen.getByTestId('reporting-status');

			expect(within(status).getByTestId('reporting-badge')).toHaveTextContent('Active (web-01)');
			expect(status).toHaveTextContent('Last snapshot 5m ago');
		});

		it('says an off cluster is known only from what its hosts report', () => {
			renderPage(makeCluster({ reportingState: 'off' }));

			expect(screen.getByTestId('reporting-status')).toHaveTextContent(
				'Vord knows this cluster only from what its hosts report about themselves.'
			);
		});

		it('shows the cluster version once a snapshot has reported it', () => {
			renderPage(makeCluster({ reportingState: 'active', clusterVersion: 'v1.31.4+k3s1' }));

			expect(screen.getByTestId('reporting-status')).toHaveTextContent('Kubernetes v1.31.4+k3s1');
		});
	});

	describe('reporters', () => {
		// The default node is linked to web-01, so a candidate row is looked up inside its own table.
		function reporterRow(name: RegExp) {
			return within(within(screen.getByTestId('reporters-table')).getByRole('row', { name }));
		}

		it('lists each enrolled machine, marking the holder', () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01', isHolder: true }),
						makeCandidate({ machineId: 8, machineName: 'web-02' })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			const table = within(screen.getByTestId('reporters-table'));

			expect(within(table.getByRole('row', { name: /web-01/ })).getByText('Holder')).toBeInTheDocument();
			expect(within(table.getByRole('row', { name: /web-02/ })).queryByText('Holder')).not.toBeInTheDocument();
			expect(table.getByRole('link', { name: 'web-01' })).toHaveAttribute('href', '/machines/7');
		});

		it('shows the error a candidate reports, worded, and when it last reported', () => {
			renderPage(
				makeCluster({
					reportingState: 'error',
					reporterError: 'credentials_rejected',
					candidates: [
						makeCandidate({
							machineName: 'web-01',
							reporterError: 'credentials_rejected',
							lastReportAt: '2026-10-11T11:30:00Z'
						})
					]
				})
			);

			const row = reporterRow(/web-01/);

			expect(row.getByText('Credentials rejected')).toBeInTheDocument();
			expect(row.getByText('30m ago')).toBeInTheDocument();
		});

		it('words the state a candidate copied from another host, with the code as its title', () => {
			renderPage(
				makeCluster({
					reportingState: 'error',
					reporterError: 'state_copied',
					candidates: [makeCandidate({ machineName: 'web-01', reporterError: 'state_copied' })]
				})
			);

			const row = reporterRow(/web-01/);

			expect(
				row.getByText('Reporter state was copied from another host; unenrol or re-enrol this host')
			).toHaveAttribute('title', 'state_copied');
		});

		it('makes a readable label of an error code it has no wording for, and keeps the code as its title', () => {
			renderPage(
				makeCluster({
					candidates: [makeCandidate({ machineName: 'web-01', reporterError: 'disk_full' })]
				})
			);

			const row = reporterRow(/web-01/);

			expect(row.getByText('Disk full')).toHaveAttribute('title', 'disk_full');
		});

		it('shows a placeholder for a candidate that reports no error', () => {
			renderPage(makeCluster({ candidates: [makeCandidate({ machineName: 'web-01', reporterError: null })] }));

			expect(reporterRow(/web-01/).getAllByRole('cell')[2]).toHaveTextContent('---');
		});

		it('says whether a candidate is online', () => {
			renderPage(
				makeCluster({
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01', online: true }),
						makeCandidate({ machineId: 8, machineName: 'web-02', online: false })
					]
				})
			);

			expect(reporterRow(/web-01/).getByText('Online')).toBeInTheDocument();
			expect(reporterRow(/web-02/).getByText('Offline')).toBeInTheDocument();
		});

		it('shows no reporters table when none is enrolled', () => {
			renderPage(makeCluster({ candidates: [] }));

			expect(screen.queryByTestId('reporters-table')).not.toBeInTheDocument();
		});
	});

	describe('enable cluster reporting', () => {
		const offCluster = (eligible: KubernetesEligibleMachineDto[]) =>
			makeCluster({ reportingState: 'off', eligibleMachines: eligible });

		it('is offered to a machine admin on an off cluster with an eligible machine', () => {
			renderPage(offCluster([makeEligible()]));

			expect(screen.getByRole('button', { name: 'Enable cluster reporting' })).toBeEnabled();
		});

		it('is not offered when no machine is eligible', () => {
			renderPage(offCluster([]));

			expect(screen.queryByRole('button', { name: 'Enable cluster reporting' })).not.toBeInTheDocument();
		});

		it('is not offered to a viewer', () => {
			renderPage(offCluster([makeEligible()]), { role: UserAccountRole.Viewer });

			expect(screen.queryByRole('button', { name: 'Enable cluster reporting' })).not.toBeInTheDocument();
		});

		it.each(['active', 'stale', 'error', 'role_outdated'] as const)(
			'is not offered once reporting is %s',
			(reportingState) => {
				renderPage(makeCluster({ reportingState, eligibleMachines: [makeEligible()], candidates: [makeCandidate()] }));

				expect(screen.queryByRole('button', { name: 'Enable cluster reporting' })).not.toBeInTheDocument();
			}
		);

		it('opens a dialog with the full warning, word for word', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));

			const dialog = await screen.findByRole('dialog', { name: 'Enable cluster reporting' });

			expect(within(dialog).getByText(ENABLE_TEXT)).toBeInTheDocument();
		});

		it('states that enrolling needs remote commands on the host and that turning reporting off works on any host', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));

			const dialog = await screen.findByRole('dialog');

			expect(
				within(dialog).getByText(
					'Enrolling a host needs remote commands enabled on that host (allow_remote_commands in the agent config). ' +
						'Turning reporting off works on any host.'
				)
			).toBeInTheDocument();
		});

		it('says nothing about the subscription tier, because reporting is open to every plan', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));

			const dialog = await screen.findByRole('dialog');

			expect(dialog).not.toHaveTextContent(/\btier\b|subscription|\bTeam\b|your plan/i);
		});

		it('sends nothing until the user confirms', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await screen.findByRole('dialog');

			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('sends nothing when the user cancels', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

			expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('signs an enrol for each machine the key is authorised on, and lists the rest as unauthorised', async () => {
			renderPage(
				offCluster([
					makeEligible({ machineId: 7, machineName: 'web-01', authorizedSigningKeyIds: [41] }),
					makeEligible({ machineId: 8, machineName: 'web-02', authorizedSigningKeyIds: [99] }),
					makeEligible({ machineId: 9, machineName: 'web-03', authorizedSigningKeyIds: [41, 99] })
				])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			const dialog = await screen.findByRole('dialog');

			const unauthorised = within(dialog).getByTestId('unauthorised-machines');
			expect(unauthorised).toHaveTextContent('Your signing key is not authorised on these machines');
			expect(unauthorised).toHaveTextContent('web-02');
			expect(unauthorised).not.toHaveTextContent('web-01');

			await fireEvent.click(within(dialog).getByRole('button', { name: 'Enable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentSummary()).toEqual([
				[7, 'k8s_reporter_enrol'],
				[9, 'k8s_reporter_enrol']
			]);
		});

		it('signs each command with the key that is authorised on that machine', async () => {
			getLocalKeys.mockResolvedValue([
				{ id: 3, label: 'Laptop', publicKeyBase64: 'PUBLIC-A', createdAt: '' },
				{ id: 4, label: 'Desk', publicKeyBase64: 'PUBLIC-B', createdAt: '' }
			]);
			api.getSigningKeys.mockResolvedValue({
				keys: [SERVER_KEY, { ...SERVER_KEY, id: 42, label: 'Desk', publicKey: 'PUBLIC-B' }],
				activeCount: 2,
				maxKeys: 5
			});
			renderPage(
				offCluster([
					makeEligible({ machineId: 7, machineName: 'web-01', authorizedSigningKeyIds: [41] }),
					makeEligible({ machineId: 8, machineName: 'web-02', authorizedSigningKeyIds: [42] })
				])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await waitFor(() => expect(api.sendCommand).toHaveBeenCalledTimes(2));

			expect(sentCommands().map((c) => [c.machineId, c.signingKeyId])).toEqual([
				[7, 41],
				[8, 42]
			]);
		});

		it('sends the signed payload with an empty params object and a null Params', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await waitFor(() => expect(api.sendCommand).toHaveBeenCalledTimes(1));

			const request = sentCommands()[0];

			expect(request.canonicalPayload).toContain('"params":{}');
			expect(request.params).toBeNull();
			expect(signPayload).toHaveBeenCalledWith(3, request.canonicalPayload);
		});

		it('shows a per-machine result: accepted, or the API message', async () => {
			api.sendCommand.mockImplementation(async (request: SendCommandRequest) => {
				if (request.machineId === 8) {
					throw new ApiError(400, 'Only control-plane machines with an admin credential can enrol');
				}

				return {};
			});
			renderPage(
				offCluster([
					makeEligible({ machineId: 7, machineName: 'web-01' }),
					makeEligible({ machineId: 8, machineName: 'web-02' })
				])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));

			const results = within(await screen.findByTestId('command-results')).getAllByRole('listitem');

			expect(results[0]).toHaveTextContent('web-01');
			expect(results[0]).toHaveAttribute('data-outcome', 'accepted');
			expect(results[1]).toHaveTextContent('web-02');
			expect(results[1]).toHaveAttribute('data-outcome', 'failed');
			expect(results[1]).toHaveTextContent('Only control-plane machines with an admin credential can enrol');
		});

		it('keeps sending to the other machines when one is refused', async () => {
			api.sendCommand.mockRejectedValueOnce(new ApiError(409, 'This host is paused'));
			renderPage(
				offCluster([
					makeEligible({ machineId: 7, machineName: 'web-01' }),
					makeEligible({ machineId: 8, machineName: 'web-02' })
				])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([
				[7, 'k8s_reporter_enrol'],
				[8, 'k8s_reporter_enrol']
			]);
		});

		it('shows the Team-tier refusal against each machine', async () => {
			api.sendCommand.mockRejectedValue(new ApiError(403, 'Remote commands require a Team subscription'));
			renderPage(offCluster([makeEligible({ machineId: 7, machineName: 'web-01' })]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));

			expect(await screen.findByTestId('command-results')).toHaveTextContent(
				'Remote commands require a Team subscription'
			);
		});

		it('shows a bare Forbidden as it is, without guessing that the plan is the cause', async () => {
			api.sendCommand.mockRejectedValue(new ApiError(403, 'Forbidden'));
			renderPage(offCluster([makeEligible({ machineId: 7, machineName: 'web-01' })]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));

			const results = await screen.findByTestId('command-results');

			expect(results).toHaveTextContent('Forbidden');
			expect(results).not.toHaveTextContent('Team subscription');
		});

		it('does not send to a host with remote commands disabled or a paused host, and says why', async () => {
			renderPage(
				offCluster([
					makeEligible({ machineId: 7, machineName: 'web-01' }),
					makeEligible({ machineId: 8, machineName: 'web-02', remoteCommandsEnabled: false }),
					makeEligible({ machineId: 9, machineName: 'web-03', isPaused: true })
				])
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			const dialog = await screen.findByRole('dialog');

			const skipped = within(dialog).getByTestId('skipped-machines');
			expect(skipped).toHaveTextContent('web-02');
			expect(skipped).toHaveTextContent(
				'Remote commands are disabled on this host (allow_remote_commands in the agent config)'
			);
			expect(skipped).toHaveTextContent('web-03');
			expect(skipped).toHaveTextContent('This host is paused');

			await fireEvent.click(within(dialog).getByRole('button', { name: 'Enable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentSummary()).toEqual([[7, 'k8s_reporter_enrol']]);
		});

		it('still sends to a host whose remote commands setting is unknown', async () => {
			renderPage(offCluster([makeEligible({ machineId: 7, machineName: 'web-01', remoteCommandsEnabled: null })]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[7, 'k8s_reporter_enrol']]);
		});

		it('cannot be confirmed when no key from this browser is registered, and sends nothing', async () => {
			getLocalKeys.mockResolvedValue([]);
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			const dialog = await screen.findByRole('dialog');

			expect(within(dialog).getByRole('link', { name: 'Generate a signing key' })).toBeInTheDocument();
			expect(within(dialog).getByRole('button', { name: 'Enable reporting' })).toBeDisabled();
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('refreshes the page data once the commands are sent', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await screen.findByTestId('command-results');

			expect(invalidateAll).toHaveBeenCalled();
		});

		it('closes after the results have been read', async () => {
			renderPage(offCluster([makeEligible()]));

			await fireEvent.click(screen.getByRole('button', { name: 'Enable cluster reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Enable reporting' }));
			await fireEvent.click(await screen.findByRole('button', { name: 'Close' }));

			expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
		});
	});

	describe('disable reporting', () => {
		function activeCluster(overrides: Partial<KubernetesClusterDetailDto> = {}) {
			return makeCluster({
				reportingState: 'active',
				activeReporterMachineId: 8,
				activeReporterMachineName: 'web-02',
				candidates: [
					makeCandidate({ machineId: 7, machineName: 'web-01' }),
					makeCandidate({ machineId: 8, machineName: 'web-02', isHolder: true }),
					makeCandidate({ machineId: 9, machineName: 'web-03' })
				],
				eligibleMachines: [
					makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
					makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true }),
					makeEligible({ machineId: 9, machineName: 'web-03', enrolled: true })
				],
				...overrides
			});
		}

		async function openDisableDialog() {
			await fireEvent.click(screen.getByRole('button', { name: 'Disable reporting' }));

			return screen.findByRole('dialog', { name: 'Disable cluster reporting' });
		}

		it('is offered to a machine admin once reporting is on', () => {
			renderPage(activeCluster());

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeEnabled();
		});

		it('is not offered while reporting is off', () => {
			renderPage(makeCluster({ reportingState: 'off' }));

			expect(screen.queryByRole('button', { name: 'Disable reporting' })).not.toBeInTheDocument();
		});

		it('is not offered to a viewer', () => {
			renderPage(activeCluster(), { role: UserAccountRole.Viewer });

			expect(screen.queryByRole('button', { name: 'Disable reporting' })).not.toBeInTheDocument();
		});

		it('is disabled, with a reason for each machine, when no eligible machine is online', () => {
			renderPage(
				activeCluster({
					eligibleMachines: [
						makeEligible({ machineId: 7, enrolled: true, online: false }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true, online: false })
					]
				})
			);

			const reasons = screen.getByTestId('disable-blocked-machines');

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeDisabled();
			expect(screen.getByTestId('reporting-status')).toHaveTextContent('none can take the uninstall command right now');
			expect(within(reasons).getAllByRole('listitem').map((item) => item.textContent?.trim())).toEqual([
				'web-01: Offline',
				'web-02: Offline'
			]);
		});

		it('is disabled, and says why, when the only online machine is paused', () => {
			renderPage(
				activeCluster({
					eligibleMachines: [
						makeEligible({ machineId: 7, enrolled: true, isPaused: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true, online: false })
					]
				})
			);

			const reasons = within(screen.getByTestId('disable-blocked-machines'));

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeDisabled();
			expect(reasons.getByText('web-01: This host is paused')).toBeInTheDocument();
			expect(reasons.getByText('web-02: Offline')).toBeInTheDocument();
		});

		it('is disabled, and says why, when the only online machine has remote commands disabled and never enrolled', () => {
			renderPage(
				activeCluster({
					candidates: [],
					eligibleMachines: [makeEligible({ machineId: 7, remoteCommandsEnabled: false })]
				})
			);

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeDisabled();
			expect(
				within(screen.getByTestId('disable-blocked-machines')).getByText(
					'web-01: Remote commands are disabled on this host (allow_remote_commands in the agent config)'
				)
			).toBeInTheDocument();
		});

		it('is available when the only online machine has remote commands disabled but is a candidate', () => {
			renderPage(
				activeCluster({
					activeReporterMachineId: 7,
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01', isHolder: true })],
					eligibleMachines: [makeEligible({ machineId: 7, remoteCommandsEnabled: false })]
				})
			);

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeEnabled();
			expect(screen.queryByTestId('disable-blocked-machines')).not.toBeInTheDocument();
		});

		it('is available when the only online machine has remote commands disabled but reports itself enrolled', () => {
			renderPage(
				activeCluster({
					candidates: [],
					eligibleMachines: [makeEligible({ machineId: 7, enrolled: true, remoteCommandsEnabled: false })]
				})
			);

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeEnabled();
		});

		it('is disabled, and says why, once the keys load and none is authorised on the only online machine', async () => {
			renderPage(
				activeCluster({
					eligibleMachines: [makeEligible({ machineId: 7, enrolled: true, authorizedSigningKeyIds: [99] })]
				})
			);

			await waitFor(() => expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeDisabled());

			expect(
				within(screen.getByTestId('disable-blocked-machines')).getByText(
					'web-01: Your signing key is not authorised on this machine'
				)
			).toBeInTheDocument();
		});

		it('stays available when one online machine can take the uninstall, whatever keeps the others from it', () => {
			renderPage(
				activeCluster({
					eligibleMachines: [
						makeEligible({ machineId: 7, enrolled: true, isPaused: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeEnabled();
			expect(screen.queryByTestId('disable-blocked-machines')).not.toBeInTheDocument();
		});

		it('uninstalls through the holder and unenrols every other online candidate', async () => {
			renderPage(activeCluster());

			const dialog = await openDisableDialog();
			await fireEvent.click(within(dialog).getByRole('button', { name: 'Disable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentSummary()).toEqual([
				[8, 'k8s_reporter_uninstall'],
				[7, 'k8s_reporter_unenrol'],
				[9, 'k8s_reporter_unenrol']
			]);
		});

		it('sends a single uninstall, not one per machine', async () => {
			renderPage(activeCluster());

			const dialog = await openDisableDialog();
			await fireEvent.click(within(dialog).getByRole('button', { name: 'Disable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentCommands().filter((c) => c.commandType === 'k8s_reporter_uninstall')).toHaveLength(1);
		});

		it('uninstalls through another online machine when the holder is offline, and skips the offline one with a reason', async () => {
			renderPage(
				activeCluster({
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01' }),
						makeCandidate({ machineId: 8, machineName: 'web-02', isHolder: true, online: false })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true, online: false })
					]
				})
			);

			const dialog = await openDisableDialog();

			expect(within(dialog).getByTestId('skipped-machines')).toHaveTextContent('web-02');
			expect(within(dialog).getByTestId('skipped-machines')).toHaveTextContent('Offline');

			await fireEvent.click(within(dialog).getByRole('button', { name: 'Disable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentSummary()).toEqual([[7, 'k8s_reporter_uninstall']]);
		});

		it('sends nothing, and says why, when the only online machines cannot be sent a command', async () => {
			renderPage(
				activeCluster({
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01', isHolder: true })],
					eligibleMachines: [makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true, isPaused: true })]
				})
			);

			const dialog = await openDisableDialog();

			expect(within(dialog).getByRole('button', { name: 'Disable reporting' })).toBeDisabled();
			expect(within(dialog).getByTestId('skipped-machines')).toHaveTextContent('This host is paused');
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('uninstalls through a holder whose remote commands are disabled, and unenrols a candidate in the same state', async () => {
			renderPage(
				activeCluster({
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01' }),
						makeCandidate({ machineId: 8, machineName: 'web-02', isHolder: true })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true, remoteCommandsEnabled: false }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true, remoteCommandsEnabled: false })
					]
				})
			);

			const dialog = await openDisableDialog();

			expect(within(dialog).queryByTestId('skipped-machines')).not.toBeInTheDocument();

			await fireEvent.click(within(dialog).getByRole('button', { name: 'Disable reporting' }));
			await within(dialog).findByTestId('command-results');

			expect(sentSummary()).toEqual([
				[8, 'k8s_reporter_uninstall'],
				[7, 'k8s_reporter_unenrol']
			]);
		});

		it('does not uninstall through a machine with remote commands disabled that never enrolled', async () => {
			renderPage(
				activeCluster({
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01', isHolder: true, online: false })],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true, online: false }),
						makeEligible({ machineId: 9, machineName: 'web-03', remoteCommandsEnabled: false })
					]
				})
			);

			expect(screen.getByRole('button', { name: 'Disable reporting' })).toBeDisabled();
			expect(
				within(screen.getByTestId('disable-blocked-machines')).getByText(
					'web-03: Remote commands are disabled on this host (allow_remote_commands in the agent config)'
				)
			).toBeInTheDocument();
		});

		it('explains that revoking the tokens is the effect, before anything is sent', async () => {
			renderPage(activeCluster());

			const dialog = await openDisableDialog();

			expect(dialog).toHaveTextContent('revokes every token');
			expect(api.sendCommand).not.toHaveBeenCalled();
		});
	});

	describe('enrolling and unenrolling one machine', () => {
		it('offers Enrol on each eligible machine that is not enrolled yet', () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01' })],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02' })
					]
				})
			);

			const table = within(screen.getByTestId('eligible-table'));

			expect(within(table.getByRole('row', { name: /web-02/ })).getByRole('button', { name: 'Enrol' })).toBeEnabled();
			expect(table.queryByRole('row', { name: /web-01/ })).not.toBeInTheDocument();
		});

		it('signs and sends one enrol for the machine in the row, and shows its result', async () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01' })],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02' })
					]
				})
			);

			await fireEvent.click(
				within(within(screen.getByTestId('eligible-table')).getByRole('row', { name: /web-02/ })).getByRole(
					'button',
					{ name: 'Enrol' }
				)
			);

			const results = await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[8, 'k8s_reporter_enrol']]);
			expect(results).toHaveTextContent('web-02');
			expect(results).toHaveTextContent('Accepted');
		});

		it('shows the API message when the enrol is refused', async () => {
			api.sendCommand.mockRejectedValue(
				new ApiError(400, 'Only control-plane machines with an admin credential can enrol')
			);
			renderPage(
				makeCluster({ reportingState: 'off', eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02' })] })
			);

			await fireEvent.click(within(screen.getByRole('row', { name: /web-02/ })).getByRole('button', { name: 'Enrol' }));

			expect(await screen.findByTestId('command-results')).toHaveTextContent(
				'Only control-plane machines with an admin credential can enrol'
			);
		});

		it('offers no Enrol to a viewer', () => {
			renderPage(makeCluster({ reportingState: 'off', eligibleMachines: [makeEligible()] }), {
				role: UserAccountRole.Viewer
			});

			expect(screen.queryByRole('button', { name: 'Enrol' })).not.toBeInTheDocument();
		});

		it('lists a host with remote commands disabled, says so, and does not let it be sent a command', async () => {
			renderPage(
				makeCluster({
					reportingState: 'off',
					eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02', remoteCommandsEnabled: false })]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-02/ }));

			expect(
				row.getByText('Remote commands are disabled on this host (allow_remote_commands in the agent config)')
			).toBeInTheDocument();
			expect(row.getByRole('button', { name: 'Enrol' })).toBeDisabled();
			await fireEvent.click(row.getByRole('button', { name: 'Enrol' }));
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('lists a paused host as paused and does not let it be sent a command', () => {
			renderPage(
				makeCluster({
					reportingState: 'off',
					eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02', isPaused: true })]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-02/ }));

			expect(row.getByText('This host is paused')).toBeInTheDocument();
			expect(row.getByRole('button', { name: 'Enrol' })).toBeDisabled();
		});

		it('shows a host whose remote commands setting is unreadable as unknown, and lets it be sent a command', () => {
			renderPage(
				makeCluster({
					reportingState: 'off',
					eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02', remoteCommandsEnabled: null })]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-02/ }));

			expect(row.getByText('Unknown')).toBeInTheDocument();
			expect(row.getByRole('button', { name: 'Enrol' })).toBeEnabled();
		});

		it('says a host the key is not authorised on cannot be enrolled from here', async () => {
			renderPage(
				makeCluster({
					reportingState: 'off',
					eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02', authorizedSigningKeyIds: [99] })]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-02/ }));

			await waitFor(() =>
				expect(row.getByText('Your signing key is not authorised on this machine')).toBeInTheDocument()
			);
			expect(row.getByRole('button', { name: 'Enrol' })).toBeDisabled();
		});

		it('offers Unenrol on each candidate, and sends one unenrol for it', async () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01' }),
						makeCandidate({ machineId: 9, machineName: 'web-03' })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 9, machineName: 'web-03', enrolled: true })
					]
				})
			);

			const table = within(screen.getByTestId('reporters-table'));
			await fireEvent.click(
				within(table.getByRole('row', { name: /web-03/ })).getByRole('button', { name: 'Unenrol' })
			);
			await screen.findByTestId('command-results');

			expect(table.getAllByRole('button', { name: 'Unenrol' })).toHaveLength(2);
			expect(sentSummary()).toEqual([[9, 'k8s_reporter_unenrol']]);
		});

		it('unenrols a candidate that returned after reporting was disabled, even if it is not described as eligible', async () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate({ machineId: 9, machineName: 'web-03' })],
					eligibleMachines: []
				})
			);

			await fireEvent.click(
				within(screen.getByRole('row', { name: /web-03/ })).getByRole('button', { name: 'Unenrol' })
			);
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[9, 'k8s_reporter_unenrol']]);
		});

		it('offers Unenrol on a candidate whose remote commands are disabled, and sends it', async () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate({ machineId: 9, machineName: 'web-03' })],
					eligibleMachines: [
						makeEligible({ machineId: 9, machineName: 'web-03', enrolled: true, remoteCommandsEnabled: false })
					]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-03/ }));

			expect(row.queryByText(/allow_remote_commands/)).not.toBeInTheDocument();
			expect(row.getByRole('button', { name: 'Unenrol' })).toBeEnabled();

			await fireEvent.click(row.getByRole('button', { name: 'Unenrol' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[9, 'k8s_reporter_unenrol']]);
		});

		it('offers a viewer no Unenrol', () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate()],
					eligibleMachines: [makeEligible({ enrolled: true })]
				}),
				{ role: UserAccountRole.Viewer }
			);

			expect(screen.queryByRole('button', { name: 'Unenrol' })).not.toBeInTheDocument();
		});

		it('does not send an unenrol to a candidate on a paused host', () => {
			renderPage(
				makeCluster({
					reportingState: 'active',
					candidates: [makeCandidate({ machineId: 9, machineName: 'web-03' })],
					eligibleMachines: [makeEligible({ machineId: 9, machineName: 'web-03', enrolled: true, isPaused: true })]
				})
			);

			const row = within(screen.getByRole('row', { name: /web-03/ }));

			expect(row.getByText('This host is paused')).toBeInTheDocument();
			expect(row.getByRole('button', { name: 'Unenrol' })).toBeDisabled();
		});

		it('can dismiss the results', async () => {
			renderPage(
				makeCluster({ reportingState: 'off', eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02' })] })
			);

			await fireEvent.click(within(screen.getByRole('row', { name: /web-02/ })).getByRole('button', { name: 'Enrol' }));
			await screen.findByTestId('command-results');
			await fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

			expect(screen.queryByTestId('command-results')).not.toBeInTheDocument();
		});

		it('says an offline host must receive the command within five minutes', async () => {
			renderPage(
				makeCluster({
					reportingState: 'off',
					eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02', online: false })]
				})
			);

			await fireEvent.click(within(screen.getByRole('row', { name: /web-02/ })).getByRole('button', { name: 'Enrol' }));

			const results = await screen.findByTestId('command-results');

			expect(results).toHaveTextContent('it will refuse the command unless it receives it within 5 minutes');
			expect(results).not.toHaveTextContent('10 minutes');
		});
	});

	describe('banners', () => {
		it('shows the truncation banner', () => {
			renderPage(makeCluster({ truncated: true }));

			expect(screen.getByTestId('truncated-banner')).toBeInTheDocument();
		});

		it('shows the stale banner with the time the details date from', () => {
			const staleSince = '2026-10-11T06:00:00Z';
			renderPage(makeCluster({ reportingState: 'stale', staleSince, candidates: [makeCandidate()] }));

			expect(screen.getByTestId('stale-banner')).toHaveTextContent(`Stale since ${formatDateTime(staleSince)}`);
		});

		it('shows the role outdated banner', () => {
			renderPage(makeCluster({ reportingState: 'role_outdated', candidates: [makeCandidate()] }));

			expect(screen.getByTestId('role-outdated-banner')).toBeInTheDocument();
		});

		it.each([
			['credentials_rejected', 'credentials-rejected-banner'],
			['tls_ca_changed', 'tls-ca-changed-banner'],
			['state_copied', 'state-copied-banner'],
			['api_unreachable', 'api-unreachable-banner'],
			['namespace_terminating', 'namespace-terminating-banner']
		])('shows the banner for %s', (reporterError, testId) => {
			renderPage(makeCluster({ reportingState: 'error', reporterError, candidates: [makeCandidate()] }));

			expect(screen.getByTestId(testId)).toBeInTheDocument();
		});

		it('re-enrols the candidates that report the error when Re-enrol is pressed', async () => {
			renderPage(
				makeCluster({
					reportingState: 'error',
					reporterError: 'credentials_rejected',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01', reporterError: 'credentials_rejected' }),
						makeCandidate({ machineId: 8, machineName: 'web-02', reporterError: null })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Re-enrol' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[7, 'k8s_reporter_enrol']]);
		});

		it('re-enrols only the host whose reporter state was copied when Re-enrol is pressed', async () => {
			renderPage(
				makeCluster({
					reportingState: 'error',
					reporterError: 'state_copied',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01', reporterError: null }),
						makeCandidate({ machineId: 8, machineName: 'web-02', reporterError: 'state_copied' })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Re-enrol' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[8, 'k8s_reporter_enrol']]);
		});

		it('re-enrols every candidate when none reports the error itself', async () => {
			renderPage(
				makeCluster({
					reportingState: 'error',
					reporterError: 'tls_ca_changed',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01' }),
						makeCandidate({ machineId: 8, machineName: 'web-02' })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Re-enrol' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([
				[7, 'k8s_reporter_enrol'],
				[8, 'k8s_reporter_enrol']
			]);
		});

		it('signs one enrol, through the holder, when Update permissions is pressed', async () => {
			renderPage(
				makeCluster({
					reportingState: 'role_outdated',
					activeReporterMachineId: 8,
					activeReporterMachineName: 'web-02',
					candidates: [
						makeCandidate({ machineId: 7, machineName: 'web-01' }),
						makeCandidate({ machineId: 8, machineName: 'web-02', isHolder: true })
					],
					eligibleMachines: [
						makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true }),
						makeEligible({ machineId: 8, machineName: 'web-02', enrolled: true })
					]
				})
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Update permissions' }));
			await screen.findByTestId('command-results');

			expect(sentSummary()).toEqual([[8, 'k8s_reporter_enrol']]);
		});

		it('shows the reasons when Update permissions finds no machine it can send to', async () => {
			renderPage(
				makeCluster({
					reportingState: 'role_outdated',
					candidates: [makeCandidate({ machineId: 7, machineName: 'web-01', isHolder: true })],
					eligibleMachines: [makeEligible({ machineId: 7, machineName: 'web-01', enrolled: true, isPaused: true })]
				})
			);

			await fireEvent.click(screen.getByRole('button', { name: 'Update permissions' }));

			expect(await screen.findByTestId('command-results')).toHaveTextContent('This host is paused');
			expect(api.sendCommand).not.toHaveBeenCalled();
		});

		it('offers a viewer no banner action', () => {
			renderPage(
				makeCluster({ reportingState: 'error', reporterError: 'credentials_rejected', candidates: [makeCandidate()] }),
				{ role: UserAccountRole.Viewer }
			);

			expect(screen.queryByRole('button', { name: 'Re-enrol' })).not.toBeInTheDocument();
		});
	});

	describe('without a signing key', () => {
		it('tells a machine admin to generate one, and disables the row actions', async () => {
			getLocalKeys.mockResolvedValue([]);
			renderPage(
				makeCluster({ reportingState: 'off', eligibleMachines: [makeEligible({ machineId: 8, machineName: 'web-02' })] })
			);

			expect(await screen.findByTestId('no-signing-key')).toHaveTextContent('No signing key');
			expect(within(screen.getByRole('row', { name: /web-02/ })).getByRole('button', { name: 'Enrol' })).toBeDisabled();
		});

		it('says nothing to a viewer', () => {
			getLocalKeys.mockResolvedValue([]);
			renderPage(makeCluster({ eligibleMachines: [makeEligible()] }), { role: UserAccountRole.Viewer });

			expect(screen.queryByTestId('no-signing-key')).not.toBeInTheDocument();
		});

		it('loads no keys for a viewer', () => {
			renderPage(makeCluster({ eligibleMachines: [makeEligible()] }), { role: UserAccountRole.Viewer });

			expect(getLocalKeys).not.toHaveBeenCalled();
			expect(api.getSigningKeys).not.toHaveBeenCalled();
		});
	});
});
