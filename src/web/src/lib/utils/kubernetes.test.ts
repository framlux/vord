// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { distroLabel } from './kubernetes';

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
