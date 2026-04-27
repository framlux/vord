<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { getVitalColor, getVitalSeverity } from '$lib/utils/health-colors';

	let {
		value,
		label,
		unit = '%'
	}: {
		value: number | null;
		label: string;
		unit?: string;
	} = $props();

	const displayValue = $derived(value !== null ? Math.round(value) : null);
	const severity = $derived(displayValue !== null ? getVitalSeverity(displayValue) : 'unknown');
	const ariaLabel = $derived(
		displayValue !== null
			? `${label}: ${displayValue}${unit}, ${severity}`
			: `${label}: no data`
	);

	const svgSize = 80;
	const strokeWidth = 6;
	const radius = (svgSize - strokeWidth) / 2;
	const circumference = Math.PI * radius;
	const dashOffset = $derived(
		displayValue !== null ? circumference - (circumference * Math.min(displayValue, 100)) / 100 : circumference
	);
	const arcColor = $derived(displayValue !== null ? getVitalColor(displayValue) : '#6E6E77');
</script>

<div class="flex flex-col items-center gap-1" role="meter" aria-label={ariaLabel} aria-valuenow={displayValue ?? undefined} aria-valuemin={0} aria-valuemax={100}>
	<div class="relative">
		<svg width={svgSize} height={svgSize / 2 + strokeWidth} viewBox="0 0 {svgSize} {svgSize / 2 + strokeWidth}" fill="none" aria-hidden="true">
			<!-- Track -->
			<path
				d="M {strokeWidth / 2} {svgSize / 2} A {radius} {radius} 0 0 1 {svgSize - strokeWidth / 2} {svgSize / 2}"
				stroke="currentColor"
				stroke-width={strokeWidth}
				stroke-linecap="round"
				fill="none"
				class="text-surface-200 dark:text-surface-700"
			/>
			<!-- Filled arc -->
			{#if displayValue !== null}
				<path
					d="M {strokeWidth / 2} {svgSize / 2} A {radius} {radius} 0 0 1 {svgSize - strokeWidth / 2} {svgSize / 2}"
					stroke={arcColor}
					stroke-width={strokeWidth}
					stroke-linecap="round"
					fill="none"
					stroke-dasharray={circumference}
					stroke-dashoffset={dashOffset}
					class="machine-gauge-fill"
				/>
			{/if}
		</svg>
		<!-- Value overlay -->
		<div class="absolute inset-x-0 bottom-0 text-center">
			<span class="text-lg font-bold tabular-nums" style="color: {arcColor}">
				{displayValue !== null ? displayValue : '--'}{unit}
			</span>
		</div>
	</div>
	<span class="text-xs font-medium text-surface-500 dark:text-surface-400">{label}</span>
</div>
