// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import { MachineHealthStatus } from '$lib/api/types';
import MachineStateBadge from './MachineStateBadge.svelte';

describe('MachineStateBadge', () => {
	it('shows Paused in place of a frozen Critical health, because the host is not reporting it', () => {
		render(MachineStateBadge, { props: { status: MachineHealthStatus.Critical, isPaused: true } });

		const badge = screen.getByLabelText('Status: Paused');

		expect(badge).toHaveTextContent('Paused');
		expect(screen.queryByText('Critical')).not.toBeInTheDocument();
		expect(screen.queryByLabelText('Status: Critical')).not.toBeInTheDocument();
	});

	it.each([
		[MachineHealthStatus.Healthy, 'Healthy'],
		[MachineHealthStatus.Warning, 'Warning'],
		[MachineHealthStatus.Critical, 'Critical'],
		[MachineHealthStatus.Offline, 'Offline']
	])('shows the health label for an active host in state %s', (status, label) => {
		render(MachineStateBadge, { props: { status, isPaused: false } });

		expect(screen.getByLabelText(`Status: ${label}`)).toHaveTextContent(label);
		expect(screen.queryByLabelText('Status: Paused')).not.toBeInTheDocument();
	});
});
