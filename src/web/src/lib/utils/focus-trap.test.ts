// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import { describe, it, expect, vi, afterEach } from 'vitest';
import { focusableWithin, trapTabKey, moveFocusInto } from './focus-trap';

// These assert where focus ENDS UP, not merely that it stayed somewhere inside the container.
// jsdom does not move focus on Tab, so a test that fires Tab and then asserts "focus is still in
// the dialog" passes against a deleted trap — the element it focused by hand is trivially still
// inside. Every assertion here names the destination, so deleting the wrap fails it.

let container: HTMLElement | null = null;

function mount(html: string): HTMLElement {
	const host = document.createElement('div');
	host.innerHTML = html;
	document.body.appendChild(host);
	container = host;

	return host;
}

afterEach(() => {
	container?.remove();
	container = null;
});

function tab(shiftKey = false): KeyboardEvent {
	return new KeyboardEvent('keydown', { key: 'Tab', shiftKey, bubbles: true, cancelable: true });
}

describe('focusableWithin', () => {
	it('returns the focusable controls in document order', () => {
		const host = mount(`
			<input id="a" />
			<select id="b"></select>
			<button id="c">c</button>
		`);

		expect(focusableWithin(host).map((el) => el.id)).toEqual(['a', 'b', 'c']);
	});

	it('skips disabled controls, which cannot hold focus', () => {
		const host = mount(`
			<button id="a">a</button>
			<button id="b" disabled>b</button>
			<input id="c" disabled />
			<button id="d">d</button>
		`);

		expect(focusableWithin(host).map((el) => el.id)).toEqual(['a', 'd']);
	});

	it('skips anything removed from the tab order', () => {
		// The dialog container itself carries tabindex="-1" so it can be focused programmatically
		// without becoming a tab stop. Including it would make the first Tab land on the dialog.
		const host = mount(`
			<div id="shell" tabindex="-1">
				<button id="a">a</button>
			</div>
		`);

		expect(focusableWithin(host).map((el) => el.id)).toEqual(['a']);
	});

	it('returns nothing for a container with no controls', () => {
		const host = mount(`<p>nothing here</p>`);

		expect(focusableWithin(host)).toEqual([]);
	});
});

describe('trapTabKey', () => {
	it('sends Tab on the last control to the first', () => {
		const host = mount(`
			<input id="first" />
			<button id="middle">m</button>
			<button id="last">l</button>
		`);
		const last = host.querySelector<HTMLElement>('#last')!;
		last.focus();

		trapTabKey(host, tab());

		expect(document.activeElement?.id).toBe('first');
	});

	it('sends Shift+Tab on the first control to the last', () => {
		const host = mount(`
			<input id="first" />
			<button id="last">l</button>
		`);
		const first = host.querySelector<HTMLElement>('#first')!;
		first.focus();

		trapTabKey(host, tab(true));

		expect(document.activeElement?.id).toBe('last');
	});

	it('prevents the browser default only when it wraps', () => {
		const host = mount(`
			<button id="first">f</button>
			<button id="last">l</button>
		`);
		host.querySelector<HTMLElement>('#last')!.focus();

		const wrapping = tab();
		const preventWrap = vi.spyOn(wrapping, 'preventDefault');
		trapTabKey(host, wrapping);

		expect(preventWrap).toHaveBeenCalled();
	});

	it('leaves a Tab in the middle of the dialog to the browser', () => {
		// Wrapping is the only thing this does. Taking over every Tab would break the natural order
		// the rest of the dialog relies on.
		const host = mount(`
			<button id="first">f</button>
			<button id="middle">m</button>
			<button id="last">l</button>
		`);
		const middle = host.querySelector<HTMLElement>('#middle')!;
		middle.focus();

		const event = tab();
		const prevented = vi.spyOn(event, 'preventDefault');
		trapTabKey(host, event);

		expect(prevented).not.toHaveBeenCalled();
		expect(document.activeElement?.id).toBe('middle');
	});

	it('ignores every key that is not Tab', () => {
		const host = mount(`<button id="only">o</button>`);
		host.querySelector<HTMLElement>('#only')!.focus();

		const event = new KeyboardEvent('keydown', { key: 'a', cancelable: true });
		const prevented = vi.spyOn(event, 'preventDefault');
		trapTabKey(host, event);

		expect(prevented).not.toHaveBeenCalled();
	});

	it('does not throw when the dialog holds nothing focusable', () => {
		const host = mount(`<p>nothing</p>`);

		expect(() => trapTabKey(host, tab())).not.toThrow();
	});
});

describe('moveFocusInto', () => {
	it('focuses the first control so the dialog is where typing goes', () => {
		const host = mount(`
			<button id="close">x</button>
			<input id="search" />
		`);

		moveFocusInto(host);

		expect(document.activeElement?.id).toBe('close');
	});

	it('falls back to the container itself when it holds no controls', () => {
		// A dialog with nothing to focus must still take focus, or the next Tab resumes from
		// wherever the user was on the page behind it and Escape never reaches the dialog.
		const host = mount(`<div id="shell" tabindex="-1"><p>nothing</p></div>`);
		const shell = host.querySelector<HTMLElement>('#shell')!;

		moveFocusInto(shell);

		expect(document.activeElement?.id).toBe('shell');
	});
});
