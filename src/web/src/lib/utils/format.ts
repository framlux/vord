// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

export function formatDate(dateStr: string | null): string {
	if (dateStr === null) return '—';
	const date = new Date(dateStr);
	if (isNaN(date.getTime())) return '—';
	return date.toLocaleDateString('en-US', {
		year: 'numeric',
		month: 'short',
		day: 'numeric'
	});
}

export function formatDateTime(dateStr: string | null): string {
	if (dateStr === null) return '—';
	const date = new Date(dateStr);
	if (isNaN(date.getTime())) return '—';
	return date.toLocaleString('en-US', {
		year: 'numeric',
		month: 'short',
		day: 'numeric',
		hour: '2-digit',
		minute: '2-digit'
	});
}

export function formatRelativeTime(dateStr: string | null): string {
	if (dateStr === null) return 'Never';
	const date = new Date(dateStr);
	const now = new Date();
	const diffMs = now.getTime() - date.getTime();
	const diffSecs = Math.floor(diffMs / 1000);
	const diffMins = Math.floor(diffSecs / 60);
	const diffHours = Math.floor(diffMins / 60);
	const diffDays = Math.floor(diffHours / 24);

	if (diffSecs < 60) return 'Just now';
	if (diffMins < 60) return `${diffMins}m ago`;
	if (diffHours < 24) return `${diffHours}h ago`;
	if (diffDays < 7) return `${diffDays}d ago`;
	return formatDate(dateStr);
}

export function formatNumber(n: number): string {
	return n.toLocaleString('en-US');
}

export function formatPercentage(value: number, total: number): string {
	if (total === 0) return '0%';
	return `${Math.round((value / total) * 100)}%`;
}

export function formatBytes(bytes: number): string {
	if (bytes === 0) return '0 B';
	if (Number.isFinite(bytes) === false) return '—';
	if (bytes < 0) return '-' + formatBytes(-bytes);
	const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
	const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
	const value = bytes / Math.pow(1024, i);
	return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[i]}`;
}

export function formatUptime(seconds: number): string {
	if (seconds <= 0) return '—';
	const days = Math.floor(seconds / 86400);
	const hours = Math.floor((seconds % 86400) / 3600);
	if (days > 0) return `${days}d ${hours}h`;
	const mins = Math.floor((seconds % 3600) / 60);
	if (hours > 0) return `${hours}h ${mins}m`;
	return `${mins}m`;
}
