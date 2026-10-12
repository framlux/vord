// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
