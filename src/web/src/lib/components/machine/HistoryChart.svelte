<!-- Copyright (c) 2026 Framlux LLC
     Licensed under the Functional Source License, Version 1.1, ALv2 Future License
     See LICENSE for details. -->

<script lang="ts">
	import { onMount } from 'svelte';
	import type { HistoryPoint } from '$lib/api/types';
	import {
		Chart,
		LineController,
		LineElement,
		PointElement,
		Filler,
		LinearScale,
		TimeScale,
		Tooltip,
		Legend
	} from 'chart.js';
	import 'chartjs-adapter-date-fns';

	Chart.register(LineController, LineElement, PointElement, Filler, LinearScale, TimeScale, Tooltip, Legend);

	let {
		points,
		label,
		color,
		unit,
		stepped = false,
		thresholds = false
	}: {
		points: HistoryPoint[];
		label: string;
		color: string;
		unit: string;
		stepped?: boolean;
		thresholds?: boolean;
	} = $props();

	let canvas: HTMLCanvasElement | undefined = $state();
	let chart: Chart | null = null;

	function hexToRgba(hex: string, alpha: number): string {
		const r = parseInt(hex.slice(1, 3), 16);
		const g = parseInt(hex.slice(3, 5), 16);
		const b = parseInt(hex.slice(5, 7), 16);

		return `rgba(${r}, ${g}, ${b}, ${alpha})`;
	}

	function isDarkMode(): boolean {
		return document.documentElement.classList.contains('dark');
	}

	function getTickColor(): string {
		return isDarkMode() ? '#9ca3af' : '#6b7280';
	}

	function getGridColor(): string {
		return isDarkMode()
			? 'rgba(156, 163, 175, 0.15)'
			: 'rgba(107, 114, 128, 0.15)';
	}

	const THRESHOLD_GREEN = '#22c55e';
	const THRESHOLD_AMBER = '#f59e0b';
	const THRESHOLD_RED = '#ef4444';

	function buildChartData() {
		const baseDataset = {
			label,
			data: points.map((p) => p.value),
			borderColor: thresholds ? THRESHOLD_GREEN : color,
			backgroundColor: hexToRgba(color, 0.2),
			fill: true,
			tension: stepped ? 0 : 0.3,
			stepped,
			pointRadius: points.length > 100 ? 0 : 2,
			pointHoverRadius: 4,
			borderWidth: 2,
			...(thresholds
				? {
						segment: {
							borderColor: (ctx: { p1: { parsed: { y: number } } }) => {
								const value = ctx.p1.parsed.y;
								if (value >= 95) return THRESHOLD_RED;
								if (value >= 80) return THRESHOLD_AMBER;

								return THRESHOLD_GREEN;
							}
						}
					}
				: {})
		};

		return {
			labels: points.map((p) => new Date(p.timestamp)),
			datasets: [baseDataset]
		};
	}

	function buildChartOptions(): object {
		return {
			responsive: true,
			maintainAspectRatio: false,
			interaction: {
				mode: 'index' as const,
				intersect: false
			},
			plugins: {
				legend: {
					display: false
				},
				tooltip: {
					callbacks: {
						label: (ctx: { parsed: { y: number } }) => `${ctx.parsed.y.toFixed(1)}${unit}`
					}
				}
			},
			scales: {
				x: {
					type: 'time' as const,
					grid: {
						display: false
					},
					ticks: {
						maxTicksLimit: 8,
						color: getTickColor()
					}
				},
				y: {
					beginAtZero: true,
					grid: {
						color: getGridColor()
					},
					ticks: {
						callback: (value: number) => `${value}${unit}`,
						color: getTickColor()
					}
				}
			}
		};
	}

	onMount(() => {
		return () => {
			if (chart !== null) {
				chart.destroy();
				chart = null;
			}
		};
	});

	$effect(() => {
		if (canvas === undefined) return;

		// Re-run when any prop changes - reading them subscribes to reactive updates
		const currentPoints = points;
		void label;
		void color;
		void unit;
		void stepped;
		void thresholds;

		if (chart !== null) {
			chart.destroy();
			chart = null;
		}

		if (currentPoints.length === 0) return;

		chart = new Chart(canvas, {
			type: 'line',
			data: buildChartData() as never,
			options: buildChartOptions() as never
		});
	});
</script>

<div class="relative h-72 w-full">
	{#if points.length === 0}
		<div class="flex h-full items-center justify-center">
			<p class="text-sm text-surface-500 dark:text-surface-400">No data available for this time range.</p>
		</div>
	{:else}
		<canvas bind:this={canvas}></canvas>
	{/if}
</div>
