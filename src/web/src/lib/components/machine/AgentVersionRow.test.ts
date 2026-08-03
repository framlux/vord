// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/svelte';
import AgentVersionRow from './AgentVersionRow.svelte';

describe('AgentVersionRow', () => {
	it('should render the reported agent version', () => {
		const { container } = render(AgentVersionRow, { props: { agentVersion: '1.16.0' } });

		const value = container.querySelector('[data-testid="agent-version"]');
		expect(value?.textContent?.trim()).toBe('1.16.0');
	});

	it('should label the row so the version is identifiable on the page', () => {
		const { container } = render(AgentVersionRow, { props: { agentVersion: '1.16.0' } });

		expect(container.textContent).toContain('Agent Version');
	});

	it('should show "Not reported" when the machine has never reported a version', () => {
		const { container } = render(AgentVersionRow, { props: { agentVersion: null } });

		const value = container.querySelector('[data-testid="agent-version"]');
		expect(value?.textContent?.trim()).toBe('Not reported');
	});

	it('should show "Not reported" for a blank version rather than an empty row', () => {
		const { container } = render(AgentVersionRow, { props: { agentVersion: '   ' } });

		const value = container.querySelector('[data-testid="agent-version"]');
		expect(value?.textContent?.trim()).toBe('Not reported');
	});
});
