<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { MachineType, MachineHealthStatus } from '$lib/api/types';
	import { getHealthColors } from '$lib/utils/health-colors';

	let {
		machineType,
		healthStatus,
		isOnline,
		size = 200
	}: {
		machineType: MachineType;
		healthStatus: MachineHealthStatus;
		isOnline: boolean;
		size?: number;
	} = $props();

	const colors = $derived(getHealthColors(healthStatus));

	const center = $derived(size / 2);
	const ringRadius = $derived(size / 2 - 8);
	const ringCircumference = $derived(2 * Math.PI * ringRadius);
	const ringArcLength = $derived(ringCircumference * 0.75);
	const ringGap = $derived(ringCircumference * 0.25);

	const filterId = $derived(`glow-${Math.random().toString(36).slice(2, 8)}`);
</script>

<svg
	width={size}
	height={size}
	viewBox="0 0 {size} {size}"
	fill="none"
	xmlns="http://www.w3.org/2000/svg"
	role="img"
	aria-label="{isOnline ? 'Online' : 'Offline'} machine visualization"
>
	<defs>
		<!-- Glow filter for machine icon -->
		<filter id={filterId} x="-50%" y="-50%" width="200%" height="200%">
			<feGaussianBlur in="SourceGraphic" stdDeviation="8" result="blur" />
			<feComposite in="blur" in2="SourceGraphic" operator="over" />
		</filter>

		<!-- Gradient for the health ring -->
		<linearGradient id="{filterId}-grad" x1="0%" y1="0%" x2="100%" y2="100%">
			<stop offset="0%" stop-color={colors.hex} stop-opacity="1" />
			<stop offset="100%" stop-color={colors.hex} stop-opacity="0.4" />
		</linearGradient>
	</defs>

	<!-- Background ring track -->
	<circle
		cx={center}
		cy={center}
		r={ringRadius}
		stroke="currentColor"
		stroke-width="2"
		fill="none"
		class="text-surface-200 dark:text-surface-700"
		stroke-dasharray="{ringArcLength} {ringGap}"
		stroke-dashoffset={ringCircumference * 0.125}
		stroke-linecap="round"
		transform="rotate(-225 {center} {center})"
	/>

	<!-- Health status arc (animated draw-in) -->
	<circle
		cx={center}
		cy={center}
		r={ringRadius}
		stroke="url(#{filterId}-grad)"
		stroke-width="3"
		fill="none"
		stroke-dasharray="{ringArcLength} {ringGap}"
		stroke-dashoffset={ringCircumference * 0.125}
		stroke-linecap="round"
		transform="rotate(-225 {center} {center})"
		class="machine-ring-draw"
	/>

	<!-- Circuit trace decorations -->
	{#each [45, 135, 225, 315] as angle}
		{@const rad = (angle * Math.PI) / 180}
		{@const innerR = size * 0.28}
		{@const outerR = size * 0.42}
		<line
			x1={center + Math.cos(rad) * innerR}
			y1={center + Math.sin(rad) * innerR}
			x2={center + Math.cos(rad) * outerR}
			y2={center + Math.sin(rad) * outerR}
			stroke={colors.hex}
			stroke-width="1"
			stroke-opacity="0.2"
			stroke-dasharray="4 4"
			class="machine-circuit-dash"
		/>
		<!-- Small terminal dots at ends of circuit lines -->
		<circle
			cx={center + Math.cos(rad) * outerR}
			cy={center + Math.sin(rad) * outerR}
			r="2"
			fill={colors.hex}
			fill-opacity="0.3"
		/>
	{/each}

	<!-- Glow behind machine icon -->
	<circle
		cx={center}
		cy={center}
		r={size * 0.18}
		fill={colors.hex}
		filter="url(#{filterId})"
		class="machine-glow-pulse"
		opacity="0.15"
	/>

	<!-- Machine type silhouette -->
	<g transform="translate({center - 30}, {center - 30}) scale(0.75)">
		{#if machineType === MachineType.BareMetalServer}
			<!-- 2U Rack Server -->
			<rect x="8" y="16" width="64" height="48" rx="4" stroke={colors.hex} stroke-width="2" fill="none" />
			<!-- Rack ears -->
			<rect x="2" y="20" width="6" height="8" rx="1" fill={colors.hex} fill-opacity="0.3" />
			<rect x="72" y="20" width="6" height="8" rx="1" fill={colors.hex} fill-opacity="0.3" />
			<rect x="2" y="52" width="6" height="8" rx="1" fill={colors.hex} fill-opacity="0.3" />
			<rect x="72" y="52" width="6" height="8" rx="1" fill={colors.hex} fill-opacity="0.3" />
			<!-- Ventilation lines -->
			<line x1="16" y1="28" x2="56" y2="28" stroke={colors.hex} stroke-width="1" stroke-opacity="0.4" />
			<line x1="16" y1="34" x2="56" y2="34" stroke={colors.hex} stroke-width="1" stroke-opacity="0.4" />
			<line x1="16" y1="40" x2="56" y2="40" stroke={colors.hex} stroke-width="1" stroke-opacity="0.4" />
			<!-- LED indicators -->
			<circle cx="64" cy="28" r="2.5" fill={isOnline ? '#10B981' : '#6E6E77'} class={isOnline ? 'machine-led-pulse' : ''} />
			<circle cx="64" cy="36" r="2.5" fill={colors.hex} fill-opacity="0.5" />
			<!-- Drive bays bottom section -->
			<line x1="8" y1="46" x2="72" y2="46" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />
			<rect x="14" y="50" width="12" height="8" rx="1" stroke={colors.hex} stroke-width="1" fill="none" stroke-opacity="0.4" />
			<rect x="30" y="50" width="12" height="8" rx="1" stroke={colors.hex} stroke-width="1" fill="none" stroke-opacity="0.4" />
			<rect x="46" y="50" width="12" height="8" rx="1" stroke={colors.hex} stroke-width="1" fill="none" stroke-opacity="0.4" />

		{:else if machineType === MachineType.Desktop}
			<!-- Tower Case -->
			<rect x="16" y="8" width="48" height="64" rx="4" stroke={colors.hex} stroke-width="2" fill="none" />
			<!-- Power button -->
			<circle cx="40" cy="18" r="4" stroke={colors.hex} stroke-width="1.5" fill="none" />
			<line x1="40" y1="15" x2="40" y2="18" stroke={colors.hex} stroke-width="1.5" />
			<!-- LED -->
			<circle cx="32" cy="18" r="2" fill={isOnline ? '#10B981' : '#6E6E77'} class={isOnline ? 'machine-led-pulse' : ''} />
			<!-- Drive bay -->
			<rect x="22" y="28" width="36" height="10" rx="2" stroke={colors.hex} stroke-width="1" fill="none" stroke-opacity="0.5" />
			<!-- Ventilation pattern -->
			<line x1="24" y1="48" x2="56" y2="48" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />
			<line x1="24" y1="52" x2="56" y2="52" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />
			<line x1="24" y1="56" x2="56" y2="56" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />
			<line x1="24" y1="60" x2="56" y2="60" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />
			<line x1="24" y1="64" x2="56" y2="64" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" />

		{:else if machineType === MachineType.Laptop}
			<!-- Screen -->
			<rect x="14" y="8" width="52" height="36" rx="3" stroke={colors.hex} stroke-width="2" fill="none" />
			<!-- Screen inner -->
			<rect x="18" y="12" width="44" height="28" rx="1" fill={colors.hex} fill-opacity="0.08" />
			<!-- Camera dot -->
			<circle cx="40" cy="10" r="1.5" fill={colors.hex} fill-opacity="0.4" />
			<!-- Hinge -->
			<line x1="12" y1="44" x2="68" y2="44" stroke={colors.hex} stroke-width="2" />
			<!-- Keyboard base -->
			<path d="M8 44 L12 44 L12 48 Q12 56 16 56 L64 56 Q68 56 68 48 L68 44 L72 44 L72 58 Q72 64 66 64 L14 64 Q8 64 8 58 Z" stroke={colors.hex} stroke-width="1.5" fill="none" />
			<!-- Keyboard keys (simplified grid) -->
			<rect x="18" y="48" width="44" height="3" rx="0.5" fill={colors.hex} fill-opacity="0.15" />
			<rect x="18" y="53" width="44" height="3" rx="0.5" fill={colors.hex} fill-opacity="0.15" />
			<rect x="26" y="58" width="28" height="3" rx="0.5" fill={colors.hex} fill-opacity="0.15" />
			<!-- Power LED -->
			<circle cx="40" cy="62" r="1.5" fill={isOnline ? '#10B981' : '#6E6E77'} class={isOnline ? 'machine-led-pulse' : ''} />

		{:else if machineType === MachineType.VirtualMachine}
			<!-- Cloud outline -->
			<path
				d="M24 48 Q12 48 12 38 Q12 28 22 26 Q24 16 36 14 Q48 12 54 20 Q56 18 60 18 Q68 18 68 26 Q76 28 76 36 Q76 46 66 48 Z"
				stroke={colors.hex}
				stroke-width="2"
				fill="none"
			/>
			<!-- Circuit traces inside cloud -->
			<line x1="28" y1="32" x2="52" y2="32" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" stroke-dasharray="3 3" />
			<line x1="32" y1="38" x2="48" y2="38" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" stroke-dasharray="3 3" />
			<line x1="40" y1="26" x2="40" y2="44" stroke={colors.hex} stroke-width="1" stroke-opacity="0.3" stroke-dasharray="3 3" />
			<!-- Node dots -->
			<circle cx="28" cy="32" r="2.5" fill={colors.hex} fill-opacity="0.5" />
			<circle cx="52" cy="32" r="2.5" fill={colors.hex} fill-opacity="0.5" />
			<circle cx="40" cy="38" r="2.5" fill={isOnline ? '#10B981' : '#6E6E77'} class={isOnline ? 'machine-led-pulse' : ''} />
			<!-- VM indicator bars below cloud -->
			<rect x="30" y="54" width="20" height="3" rx="1.5" fill={colors.hex} fill-opacity="0.3" />
			<rect x="34" y="60" width="12" height="3" rx="1.5" fill={colors.hex} fill-opacity="0.2" />

		{:else}
			<!-- Unknown / Generic Box -->
			<rect x="12" y="16" width="56" height="48" rx="4" stroke={colors.hex} stroke-width="2" fill="none" />
			<!-- Question mark -->
			<text x="40" y="46" text-anchor="middle" fill={colors.hex} fill-opacity="0.4" font-size="24" font-weight="300">?</text>
			<!-- LED -->
			<circle cx="60" cy="24" r="2" fill={isOnline ? '#10B981' : '#6E6E77'} class={isOnline ? 'machine-led-pulse' : ''} />
		{/if}
	</g>

	<!-- Online status indicator (top-right) -->
	<circle
		cx={size - 20}
		cy="20"
		r="5"
		fill={isOnline ? '#10B981' : '#6E6E77'}
		class={isOnline ? 'machine-led-pulse' : ''}
	/>
	<circle
		cx={size - 20}
		cy="20"
		r="8"
		stroke={isOnline ? '#10B981' : '#6E6E77'}
		stroke-width="1"
		fill="none"
		stroke-opacity="0.3"
	/>
</svg>
