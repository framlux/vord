// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { afterEach, beforeEach, describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import KubernetesSection from './KubernetesSection.svelte';
import type {
	KubernetesNodeDto,
	MachineKubernetesCandidateDto,
	MachineKubernetesDto,
	MachineKubernetesLocalDto
} from '$lib/api/types';

function makeLocal(overrides: Partial<MachineKubernetesLocalDto> = {}): MachineKubernetesLocalDto {
	return {
		distro: 'k3s',
		kubeletVersion: 'v1.31.4+k3s1',
		nodeName: 'cp-1',
		nodeNameSource: 'certificate',
		hasKubelet: true,
		controlPlane: true,
		adminCredentialPresent: true,
		reporterEnrolled: false,
		...overrides
	};
}

function makeNode(overrides: Partial<KubernetesNodeDto> = {}): KubernetesNodeDto {
	return {
		id: 3,
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

function makeCandidate(overrides: Partial<MachineKubernetesCandidateDto> = {}): MachineKubernetesCandidateDto {
	return {
		clusterId: 12,
		isActiveReporter: false,
		reporterError: null,
		lastReportAt: '2026-10-11T11:30:00Z',
		...overrides
	};
}

function makeKubernetes(overrides: Partial<MachineKubernetesDto> = {}): MachineKubernetesDto {
	return {
		local: makeLocal(),
		node: makeNode(),
		clusterId: 12,
		clusterName: 'Production',
		grouping: 'grouped',
		candidate: null,
		...overrides
	};
}

function text(testId: string): string {
	return screen.getByTestId(testId).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

describe('KubernetesSection', () => {
	it('renders nothing for a machine with no Kubernetes state', () => {
		const { container } = render(KubernetesSection, { props: { kubernetes: null } });

		expect(container.textContent?.trim()).toBe('');
		expect(screen.queryByTestId('kubernetes-section')).not.toBeInTheDocument();
	});

	describe('what the machine reported', () => {
		it('shows the distro label', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ local: makeLocal({ distro: 'rke2' }) }) } });

			expect(text('kubernetes-distro')).toBe('RKE2');
		});

		it('calls an undetected distribution plain Kubernetes', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ local: makeLocal({ distro: 'unknown' }) }) } });

			expect(text('kubernetes-distro')).toBe('Kubernetes');
		});

		it('shows the node name without a note when it came from the certificate', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes() } });

			expect(text('kubernetes-node-name')).toBe('cp-1');
			expect(screen.queryByTestId('kubernetes-node-name-note')).not.toBeInTheDocument();
		});

		it('notes that a node name taken from the hostname is a guess', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ local: makeLocal({ nodeName: 'host-a', nodeNameSource: 'hostname' }) }) }
			});

			expect(text('kubernetes-node-name')).toBe('host-a (from hostname)');
			expect(screen.getByTestId('kubernetes-node-name-note')).toHaveTextContent('(from hostname)');
		});

		it('shows no node name for a host without a kubelet that is not a known node', () => {
			render(KubernetesSection, {
				props: {
					kubernetes: makeKubernetes({
						local: makeLocal({ nodeName: null, nodeNameSource: null, hasKubelet: false }),
						node: null
					})
				}
			});

			expect(screen.queryByTestId('kubernetes-node-name')).not.toBeInTheDocument();
			expect(screen.queryByTestId('kubernetes-node-name-note')).not.toBeInTheDocument();
		});

		it('names a host without a kubelet after its linked node, with no hostname note', () => {
			render(KubernetesSection, {
				props: {
					kubernetes: makeKubernetes({
						local: makeLocal({ nodeName: null, nodeNameSource: null, hasKubelet: false }),
						node: makeNode({ nodeName: 'worker-9' })
					})
				}
			});

			expect(text('kubernetes-node-name')).toBe('worker-9');
			expect(screen.queryByTestId('kubernetes-node-name-note')).not.toBeInTheDocument();
		});

		it('shows the kubelet version', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes() } });

			expect(text('kubernetes-kubelet-version')).toBe('v1.31.4+k3s1');
		});

		it('shows a placeholder where the host runs no kubelet', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ local: makeLocal({ kubeletVersion: '', hasKubelet: false }) }) }
			});

			expect(text('kubernetes-kubelet-version')).toBe('---');
		});

		it('says Yes for a control plane host', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ local: makeLocal({ controlPlane: true }) }) } });

			expect(text('kubernetes-control-plane')).toBe('Yes');
		});

		it('says No for a host that is not a control plane', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ local: makeLocal({ controlPlane: false }) }) } });

			expect(text('kubernetes-control-plane')).toBe('No');
		});
	});

	describe('when the machine has not reported lately', () => {
		it('falls back to the linked node for what it knows', () => {
			render(KubernetesSection, {
				props: {
					kubernetes: makeKubernetes({
						local: null,
						node: makeNode({ nodeName: 'worker-9', controlPlane: false, kubeletVersion: 'v1.30.2' })
					})
				}
			});

			expect(text('kubernetes-node-name')).toBe('worker-9');
			expect(text('kubernetes-kubelet-version')).toBe('v1.30.2');
			expect(text('kubernetes-control-plane')).toBe('No');
			expect(screen.queryByTestId('kubernetes-distro')).not.toBeInTheDocument();
		});
	});

	describe('cluster line', () => {
		it('links a grouped machine to its cluster page by name', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ grouping: 'grouped', clusterId: 12, clusterName: 'Production' }) }
			});

			const link = within(screen.getByTestId('kubernetes-cluster')).getByRole('link', { name: 'Production' });

			expect(link).toHaveAttribute('href', '/clusters/12');
		});

		it('says the cluster is ambiguous when several clusters share the CA', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ grouping: 'ambiguous', clusterId: null, clusterName: null, node: null }) }
			});

			expect(text('kubernetes-cluster')).toBe('Several clusters share this CA. The cluster is ambiguous.');
			expect(within(screen.getByTestId('kubernetes-cluster')).queryByRole('link')).not.toBeInTheDocument();
		});

		it('says the cluster is unknown when the machine reports no CA', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ grouping: 'unknown', clusterId: null, clusterName: null, node: null }) }
			});

			expect(text('kubernetes-cluster')).toBe('Kubernetes node, cluster unknown');
		});

		it('says an ungrouped machine is not grouped yet', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ grouping: 'ungrouped', clusterId: null, clusterName: null, node: null }) }
			});

			expect(text('kubernetes-cluster')).toBe('Not grouped yet');
		});
	});

	describe('CA mismatch warning', () => {
		it('warns when the linked node reports a different CA than its cluster', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ node: makeNode({ fingerprintMismatch: true }) }) }
			});

			expect(screen.getByTestId('kubernetes-mismatch')).toHaveTextContent(
				'This node reports a different cluster CA than its cluster. This is expected during a CA rotation.'
			);
		});

		it('says nothing when the node agrees with its cluster', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ node: makeNode({ fingerprintMismatch: false }) }) }
			});

			expect(screen.queryByTestId('kubernetes-mismatch')).not.toBeInTheDocument();
		});

		it('says nothing when the machine is not a linked node', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ node: null }) } });

			expect(screen.queryByTestId('kubernetes-mismatch')).not.toBeInTheDocument();
		});
	});

	describe('reporter state', () => {
		beforeEach(() => {
			vi.useFakeTimers({ toFake: ['Date'] });
			vi.setSystemTime(new Date('2026-10-11T12:00:00Z'));
		});

		afterEach(() => {
			vi.useRealTimers();
		});

		it('shows no reporter line for a machine that is not a candidate', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ candidate: null }) } });

			expect(screen.queryByTestId('kubernetes-reporter')).not.toBeInTheDocument();
			expect(screen.queryByTestId('kubernetes-reporter-error')).not.toBeInTheDocument();
		});

		it('says a candidate that is the active reporter is reporting', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ isActiveReporter: true }) }) } });

			expect(text('kubernetes-reporter')).toBe('Reporting');
		});

		it('says a candidate that is not the active reporter is a candidate', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ isActiveReporter: false }) }) } });

			expect(text('kubernetes-reporter')).toBe('Candidate');
		});

		it('shows when the candidate last reported', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ lastReportAt: '2026-10-11T11:30:00Z' }) }) }
			});

			expect(text('kubernetes-reporter-last-report')).toBe('30m ago');
		});

		it('shows no error line for a healthy candidate', () => {
			render(KubernetesSection, { props: { kubernetes: makeKubernetes({ candidate: makeCandidate() }) } });

			expect(screen.queryByTestId('kubernetes-reporter-error')).not.toBeInTheDocument();
		});

		it('words the error a candidate reports', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ reporterError: 'credentials_rejected' }) }) }
			});

			expect(text('kubernetes-reporter-error')).toBe('Credentials rejected');
		});

		it('tells a host whose reporter state was copied to unenrol or enrol it again', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ reporterError: 'state_copied' }) }) }
			});

			expect(text('kubernetes-reporter-error')).toBe(
				'Reporter state was copied from another host; unenrol or re-enrol this host'
			);
		});

		it('makes a readable label of an error code it has no wording for', () => {
			render(KubernetesSection, {
				props: { kubernetes: makeKubernetes({ candidate: makeCandidate({ reporterError: 'disk_full' }) }) }
			});

			expect(text('kubernetes-reporter-error')).toBe('Disk full');
		});

		it('shows the reporter line for a candidate that is no node', () => {
			render(KubernetesSection, {
				props: {
					kubernetes: makeKubernetes({
						local: makeLocal({ nodeName: null, nodeNameSource: null, hasKubelet: false, reporterEnrolled: true }),
						node: null,
						candidate: makeCandidate({ isActiveReporter: true })
					})
				}
			});

			expect(text('kubernetes-reporter')).toBe('Reporting');
		});
	});
});
