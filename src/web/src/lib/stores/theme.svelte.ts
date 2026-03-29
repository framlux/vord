// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

export type Theme = 'light' | 'dark';

let currentTheme = $state<Theme>('light');

export function getTheme(): Theme {
	return currentTheme;
}

export function setTheme(value: Theme): void {
	currentTheme = value;
}
