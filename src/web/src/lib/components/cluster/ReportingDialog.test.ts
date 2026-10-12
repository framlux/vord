// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/svelte';
import '@testing-library/jest-dom/vitest';
import type { CommandResult, ReportingPlan } from '$lib/utils/clusterReporting';
import ReportingDialog from './ReportingDialog.svelte';

function plan(overrides: Partial<ReportingPlan> = {}): ReportingPlan {
	return {
		commands: [
			{ machineId: 1, machineName: 'cp-1', commandType: 'k8s_reporter_enrol', localKeyId: 3, online: true },
			{ machineId: 2, machineName: 'cp-2', commandType: 'k8s_reporter_enrol', localKeyId: 3, online: true }
		],
		skipped: [],
		...overrides
	};
}

function renderDialog(
	options: {
		plan?: ReportingPlan;
		hasKeys?: boolean;
		phase?: 'confirm' | 'sending' | 'done';
		results?: CommandResult[];
		blocked?: string | null;
		onconfirm?: () => void;
		onclose?: () => void;
	} = {}
) {
	return render(ReportingDialog, {
		props: {
			title: 'Enable cluster reporting',
			paragraphs: ['First paragraph.', 'Second paragraph.'],
			plan: options.plan ?? plan(),
			hasKeys: options.hasKeys ?? true,
			phase: options.phase ?? 'confirm',
			results: options.results ?? [],
			confirmLabel: 'Enable reporting',
			blocked: options.blocked ?? null,
			onconfirm: options.onconfirm ?? (() => {}),
			onclose: options.onclose ?? (() => {})
		}
	});
}

describe('ReportingDialog', () => {
	it('is a modal dialog named by its title', () => {
		renderDialog();

		const dialog = screen.getByRole('dialog', { name: 'Enable cluster reporting' });

		expect(dialog).toHaveAttribute('aria-modal', 'true');
	});

	it('shows each paragraph it was given', () => {
		renderDialog();

		expect(screen.getByText('First paragraph.')).toBeInTheDocument();
		expect(screen.getByText('Second paragraph.')).toBeInTheDocument();
	});

	it('lists the machines it will send to, with the command each gets', () => {
		renderDialog({
			plan: plan({
				commands: [
					{ machineId: 1, machineName: 'cp-1', commandType: 'k8s_reporter_uninstall', localKeyId: 3, online: true },
					{ machineId: 2, machineName: 'cp-2', commandType: 'k8s_reporter_unenrol', localKeyId: 3, online: true }
				]
			})
		});

		const items = within(screen.getByTestId('planned-commands')).getAllByRole('listitem');

		expect(items[0]).toHaveTextContent('cp-1');
		expect(items[0]).toHaveTextContent('Uninstall');
		expect(items[1]).toHaveTextContent('cp-2');
		expect(items[1]).toHaveTextContent('Unenrol');
	});

	it('warns that an offline host must receive the command within five minutes', () => {
		renderDialog({
			plan: plan({
				commands: [{ machineId: 1, machineName: 'cp-1', commandType: 'k8s_reporter_enrol', localKeyId: 3, online: false }]
			})
		});

		expect(screen.getByTestId('planned-commands')).toHaveTextContent('Offline');
		expect(screen.getByTestId('planned-commands')).toHaveTextContent(
			'The host must receive the command within 5 minutes, or it will refuse it.'
		);
		expect(screen.getByTestId('planned-commands')).not.toHaveTextContent('10 minutes');
	});

	describe('machines it will not send to', () => {
		it('lists the machines whose key is not authorised under their own heading', () => {
			renderDialog({
				plan: plan({
					skipped: [
						{ machineId: 3, machineName: 'cp-3', reason: 'key_not_authorised' },
						{ machineId: 4, machineName: 'cp-4', reason: 'key_not_authorised' }
					]
				})
			});

			const group = screen.getByTestId('unauthorised-machines');

			expect(within(group).getByText('Your signing key is not authorised on these machines')).toBeInTheDocument();
			expect(group).toHaveTextContent('cp-3');
			expect(group).toHaveTextContent('cp-4');
		});

		it('explains a paused host and a host with remote commands disabled', () => {
			renderDialog({
				plan: plan({
					skipped: [
						{ machineId: 3, machineName: 'cp-3', reason: 'paused' },
						{ machineId: 4, machineName: 'cp-4', reason: 'remote_commands_disabled' }
					]
				})
			});

			const group = screen.getByTestId('skipped-machines');

			expect(group).toHaveTextContent('cp-3');
			expect(group).toHaveTextContent('This host is paused');
			expect(group).toHaveTextContent('cp-4');
			expect(group).toHaveTextContent(
				'Remote commands are disabled on this host (allow_remote_commands in the agent config)'
			);
		});

		it('shows neither group when nothing is skipped', () => {
			renderDialog();

			expect(screen.queryByTestId('unauthorised-machines')).not.toBeInTheDocument();
			expect(screen.queryByTestId('skipped-machines')).not.toBeInTheDocument();
		});
	});

	describe('confirming', () => {
		it('confirms with the button', async () => {
			const onconfirm = vi.fn();
			renderDialog({ onconfirm });

			await fireEvent.click(screen.getByRole('button', { name: 'Enable reporting' }));

			expect(onconfirm).toHaveBeenCalledTimes(1);
		});

		it('closes with Cancel, without confirming', async () => {
			const onconfirm = vi.fn();
			const onclose = vi.fn();
			renderDialog({ onconfirm, onclose });

			await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

			expect(onclose).toHaveBeenCalledTimes(1);
			expect(onconfirm).not.toHaveBeenCalled();
		});

		it('closes on Escape', async () => {
			const onclose = vi.fn();
			renderDialog({ onclose });

			await fireEvent.keyDown(window, { key: 'Escape' });

			expect(onclose).toHaveBeenCalledTimes(1);
		});

		it('cannot confirm when there is nothing to send', () => {
			renderDialog({ plan: plan({ commands: [] }) });

			expect(screen.getByRole('button', { name: 'Enable reporting' })).toBeDisabled();
		});

		it('cannot confirm when the caller says it is blocked, and says why', () => {
			renderDialog({ blocked: 'No eligible machine can be sent the uninstall command right now.' });

			expect(screen.getByRole('button', { name: 'Enable reporting' })).toBeDisabled();
			expect(screen.getByText('No eligible machine can be sent the uninstall command right now.')).toBeInTheDocument();
		});

		it('does not list every machine as unauthorised when the user has no key at all', () => {
			renderDialog({
				hasKeys: false,
				plan: plan({
					commands: [],
					skipped: [
						{ machineId: 3, machineName: 'cp-3', reason: 'key_not_authorised' },
						{ machineId: 4, machineName: 'cp-4', reason: 'key_not_authorised' }
					]
				})
			});

			expect(screen.queryByTestId('unauthorised-machines')).not.toBeInTheDocument();
			expect(screen.getByRole('alert')).toHaveTextContent('No signing key');
		});

		it('says so, and offers the way to a key, when the user has none', () => {
			renderDialog({ hasKeys: false });

			expect(screen.getByRole('alert')).toHaveTextContent('No signing key');
			expect(screen.getByRole('link', { name: 'Generate a signing key' })).toHaveAttribute(
				'href',
				'/settings/signing-keys'
			);
			expect(screen.getByRole('button', { name: 'Enable reporting' })).toBeDisabled();
		});
	});

	describe('while sending', () => {
		it('disables both buttons and ignores Escape', async () => {
			const onclose = vi.fn();
			renderDialog({ phase: 'sending', onclose });

			expect(screen.getByRole('button', { name: /Enable reporting|Sending/ })).toBeDisabled();
			expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();

			await fireEvent.keyDown(window, { key: 'Escape' });

			expect(onclose).not.toHaveBeenCalled();
		});
	});

	describe('once sent', () => {
		const results: CommandResult[] = [
			{ machineId: 1, machineName: 'cp-1', commandType: 'k8s_reporter_enrol', accepted: true, message: 'Accepted' },
			{
				machineId: 2,
				machineName: 'cp-2',
				commandType: 'k8s_reporter_enrol',
				accepted: false,
				message: 'This host is paused'
			}
		];

		it('replaces the list of planned commands with the per-machine results', () => {
			renderDialog({ phase: 'done', results });

			expect(screen.queryByTestId('planned-commands')).not.toBeInTheDocument();
			expect(within(screen.getByTestId('command-results')).getAllByRole('listitem')).toHaveLength(2);
		});

		it('offers only Close', async () => {
			const onclose = vi.fn();
			renderDialog({ phase: 'done', results, onclose });

			expect(screen.queryByRole('button', { name: 'Enable reporting' })).not.toBeInTheDocument();
			await fireEvent.click(screen.getByRole('button', { name: 'Close' }));

			expect(onclose).toHaveBeenCalledTimes(1);
		});

		it('still says which machines were not sent a command', () => {
			renderDialog({
				phase: 'done',
				results,
				plan: plan({ skipped: [{ machineId: 3, machineName: 'cp-3', reason: 'paused' }] })
			});

			expect(screen.getByTestId('skipped-machines')).toHaveTextContent('cp-3');
		});
	});
});
