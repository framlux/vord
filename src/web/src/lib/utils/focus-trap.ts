// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

/**
 * The keyboard mechanics every modal dialog owes its user, in one place.
 *
 * Four dialogs used to carry their own copy of this, which is how one of them ended up with no
 * trap at all, another with a trap that skipped every control that was not a button, and a test
 * that passed with the trap deleted. Extracted as plain functions over an element rather than a
 * Svelte action so the wrapping rule can be tested against a real DOM on its own terms — asserting
 * where focus LANDS, which is the assertion the per-dialog tests were unable to make.
 */

// Deliberately every control, not just buttons: a trap that skips the search box, the filters and
// the checkboxes lets focus escape the moment someone tabs into the list. tabindex="-1" is excluded
// because the dialog container itself carries it to be focusable without becoming a tab stop.
const FOCUSABLE_SELECTOR =
	'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])';

/**
 * The focusable controls inside `container`, in document order — which is tab order here, since
 * nothing in these dialogs sets a positive tabindex.
 */
export function focusableWithin(container: HTMLElement): HTMLElement[] {
	return [...container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR)];
}

/**
 * Keeps Tab inside `container` by wrapping at its ends.
 *
 * Only the two wrapping cases are handled. Taking over every Tab would mean reimplementing the
 * browser's ordering for no gain, and getting it subtly wrong for anyone relying on it.
 */
export function trapTabKey(container: HTMLElement, event: KeyboardEvent): void {
	if (event.key !== 'Tab') {
		return;
	}

	const focusable = focusableWithin(container);
	if (focusable.length === 0) {
		return;
	}

	const first = focusable[0];
	const last = focusable[focusable.length - 1];

	if (event.shiftKey && document.activeElement === first) {
		event.preventDefault();
		last.focus();

		return;
	}

	if (event.shiftKey === false && document.activeElement === last) {
		event.preventDefault();
		first.focus();
	}
}

/**
 * Moves focus into `container` when it opens, falling back to the container itself when it holds
 * no controls.
 *
 * The fallback is the point of the function: a dialog nobody focused leaves the next Tab resuming
 * from wherever the user was on the page behind it, and leaves an Escape handler bound inside the
 * dialog unreachable — so the dialog cannot be closed from the keyboard at all.
 */
export function moveFocusInto(container: HTMLElement): void {
	const focusable = focusableWithin(container);
	if (focusable.length > 0) {
		focusable[0].focus();

		return;
	}

	container.focus();
}
