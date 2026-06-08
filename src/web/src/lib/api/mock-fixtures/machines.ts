// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type {
	MachineDto,
	MachineDetailDto,
	PaginatedResponse,
	MachineAuthorizedKeyDto
} from '../types';
import { MachineHealthStatus, OperatingSystem, MachineType } from '../types';
import { daysAgo, hoursAgo, minutesAgo, secondsAgo } from './time';

// Metadata for every machine in the mock fleet. Stays in sync with mockFleetOverview
// (same ids, names, hostnames, online state). Tuple matches the order rows appear in
// the dashboard so navigation between dashboard and /machines feels coherent.
const baseMachines: MachineDto[] = [
	{
		id: 1,
		name: 'web-01',
		description: 'Primary web tier, behind HAProxy in IAD',
		location: 'IAD / Rack 7',
		hostname: 'web-01.prod.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'DL-9TR3K22',
		assetTag: 'FXL-0011',
		isOnline: true,
		lastPing: secondsAgo(12),
		registeredOn: daysAgo(412),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 2,
		name: 'web-02',
		description: 'Primary web tier, behind HAProxy in IAD',
		location: 'IAD / Rack 7',
		hostname: 'web-02.prod.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'DL-9TR3K23',
		assetTag: 'FXL-0012',
		isOnline: true,
		lastPing: secondsAgo(8),
		registeredOn: daysAgo(412),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 3,
		name: 'db-primary',
		description: 'PostgreSQL 16 primary, streaming replication source',
		location: 'IAD / Rack 9',
		hostname: 'db-primary.prod.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'SM-2024US-A102',
		assetTag: 'FXL-0021',
		isOnline: true,
		lastPing: secondsAgo(4),
		registeredOn: daysAgo(503),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 4,
		name: 'db-replica-1',
		description: 'PostgreSQL 16 hot standby, async replication',
		location: 'IAD / Rack 9',
		hostname: 'db-replica-1.prod.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'SM-2024US-A103',
		assetTag: 'FXL-0022',
		isOnline: true,
		lastPing: secondsAgo(6),
		registeredOn: daysAgo(503),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 5,
		name: 'cache-01',
		description: 'Redis 7 cluster shard A',
		location: 'IAD / Rack 11',
		hostname: 'cache-01.iad',
		operatingSystem: OperatingSystem.Debian,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'HPE-DL380-9921',
		assetTag: 'FXL-0031',
		isOnline: true,
		lastPing: secondsAgo(9),
		registeredOn: daysAgo(287),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 6,
		name: 'cache-02',
		description: 'Redis 7 cluster shard B',
		location: 'IAD / Rack 11',
		hostname: 'cache-02.iad',
		operatingSystem: OperatingSystem.Debian,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'HPE-DL380-9922',
		assetTag: 'FXL-0032',
		isOnline: true,
		lastPing: secondsAgo(11),
		registeredOn: daysAgo(287),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 7,
		name: 'hetzner-fsn1-42',
		description: 'EU edge — Falkenstein DC',
		location: 'Hetzner FSN1',
		hostname: 'hetzner-fsn1-42',
		operatingSystem: OperatingSystem.Debian,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'HET-AX52-184201',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(15),
		registeredOn: daysAgo(189),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 8,
		name: 'hetzner-fsn1-43',
		description: 'EU edge — Falkenstein DC',
		location: 'Hetzner FSN1',
		hostname: 'hetzner-fsn1-43',
		operatingSystem: OperatingSystem.Debian,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'HET-AX52-184202',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(13),
		registeredOn: daysAgo(189),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 9,
		name: 'ovh-bhs5-12',
		description: 'CA edge — Beauharnois DC',
		location: 'OVH BHS5',
		hostname: 'ovh-bhs5-12',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'OVH-RISE1-71211',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(21),
		registeredOn: daysAgo(94),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 10,
		name: 'ovh-bhs5-13',
		description: 'CA edge — Beauharnois DC',
		location: 'OVH BHS5',
		hostname: 'ovh-bhs5-13',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'OVH-RISE1-71212',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(7),
		registeredOn: daysAgo(94),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 11,
		name: 'ci-runner-01',
		description: 'GitHub Actions self-hosted runner',
		location: 'IAD / Rack 3',
		hostname: 'ci-runner-01',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'WBX-EPYC9554-001',
		assetTag: 'FXL-0041',
		isOnline: true,
		lastPing: secondsAgo(22),
		registeredOn: daysAgo(62),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 12,
		name: 'ci-runner-02',
		description: 'GitHub Actions self-hosted runner',
		location: 'IAD / Rack 3',
		hostname: 'ci-runner-02',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'WBX-EPYC9554-002',
		assetTag: 'FXL-0042',
		isOnline: true,
		lastPing: secondsAgo(3),
		registeredOn: daysAgo(62),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 13,
		name: 'vm-monitor',
		description: 'Prometheus + Grafana host',
		location: 'IAD / vCenter cluster A',
		hostname: 'vm-monitor.k8s.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.VirtualMachine,
		serialNumber: 'VMW-VM-A-021',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(18),
		registeredOn: daysAgo(341),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 14,
		name: 'vm-logs',
		description: 'Loki + Vector aggregator',
		location: 'IAD / vCenter cluster A',
		hostname: 'vm-logs.k8s.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.VirtualMachine,
		serialNumber: 'VMW-VM-A-022',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(14),
		registeredOn: daysAgo(341),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 15,
		name: 'vm-gateway',
		description: 'WireGuard + Traefik edge gateway',
		location: 'IAD / vCenter cluster A',
		hostname: 'vm-gateway.k8s.lan',
		operatingSystem: OperatingSystem.Debian,
		machineType: MachineType.VirtualMachine,
		serialNumber: 'VMW-VM-A-023',
		assetTag: null,
		isOnline: true,
		lastPing: secondsAgo(20),
		registeredOn: daysAgo(341),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 16,
		name: 'gpu-trainer-01',
		description: 'ML training rig — 8x H100 SXM',
		location: 'IAD / Rack 14',
		hostname: 'gpu-trainer-01',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'SM-8125GS-TNHR-001',
		assetTag: 'FXL-0051',
		isOnline: true,
		lastPing: secondsAgo(5),
		registeredOn: daysAgo(48),
		isDeleted: false,
		commandsEnabled: true
	},
	{
		id: 17,
		name: 'nas-01',
		description: 'On-prem backup target (offline for maintenance)',
		location: 'Home lab',
		hostname: 'nas-01.home.lan',
		operatingSystem: OperatingSystem.Unknown,
		machineType: MachineType.BareMetalServer,
		serialNumber: 'IXSYS-MINIXP-7821',
		assetTag: null,
		isOnline: false,
		lastPing: daysAgo(2),
		registeredOn: daysAgo(721),
		isDeleted: false,
		commandsEnabled: false
	},
	{
		id: 18,
		name: 'dev-shared',
		description: 'Shared developer workstation',
		location: 'HQ / Office',
		hostname: 'dev-shared.lan',
		operatingSystem: OperatingSystem.Ubuntu,
		machineType: MachineType.Desktop,
		serialNumber: 'SYS76-MIRA-44CD',
		assetTag: 'FXL-0061',
		isOnline: true,
		lastPing: secondsAgo(28),
		registeredOn: daysAgo(154),
		isDeleted: false,
		commandsEnabled: true
	}
];

export const mockMachineList: PaginatedResponse<MachineDto> = {
	items: baseMachines,
	page: 1,
	pageSize: 25,
	totalCount: 18,
	totalPages: 1,
	hasNextPage: false,
	hasPreviousPage: false
};

export const mockMachineById: ReadonlyMap<number, MachineDto> = new Map(
	baseMachines.map((m) => [m.id, m])
);

// Stripped-down detail used as a fallback for any machine we haven't authored a
// rich detail block for. Renders the page without errors but with mostly-empty
// hardware/health panels.
function sparseDetail(machine: MachineDto, healthStatus: MachineHealthStatus): MachineDetailDto {
	return {
		id: machine.id,
		name: machine.name,
		hostname: machine.hostname,
		isOnline: machine.isOnline,
		lastPing: machine.lastPing,
		healthStatus,
		systemInfo: null,
		osVersion: null,
		cpuUsage: null,
		memoryUsage: null,
		diskUsages: null,
		hardwareHealth: null,
		packageUpdates: null,
		failedServices: [],
		totalServices: 0,
		recentSshSessions: [],
		telemetryLastUpdated: machine.lastPing
	};
}

// Rich, fully-populated detail for the hero screenshot (web-01.prod.lan).
// Hardware tab shows fans, PSUs, temps, and SMART entries for two NVMe drives.
const web01Detail: MachineDetailDto = {
	id: 1,
	name: 'web-01',
	hostname: 'web-01.prod.lan',
	isOnline: true,
	lastPing: secondsAgo(12),
	healthStatus: MachineHealthStatus.Healthy,
	systemInfo: {
		hostname: 'web-01.prod.lan',
		uuid: '4c4c4544-0054-5210-8044-b4c04f4b4b32',
		cpuType: 'x86_64',
		cpuBrand: 'Intel(R) Xeon(R) Gold 6248R @ 3.00GHz',
		cpuPhysicalCores: 48,
		cpuLogicalCores: 96,
		physicalMemory: 137_438_953_472,
		hardwareVendor: 'Dell Inc.',
		hardwareModel: 'PowerEdge R740',
		hardwareVersion: 'A05',
		hardwareSerial: 'DL-9TR3K22',
		uptimeSeconds: 4_812_337,
		biosVersion: '2.21.2',
		ipAddresses: ['10.0.1.11', 'fe80::ae1f:6bff:fe23:9a1c']
	},
	osVersion: {
		name: 'Ubuntu',
		version: '24.04.2 LTS',
		platform: 'linux',
		arch: 'x86_64',
		build: '6.8.0-51-generic'
	},
	cpuUsage: { cpuUsagePercent: 23 },
	memoryUsage: {
		memoryTotal: 137_438_953_472,
		memoryUsed: 56_348_768_153,
		memoryUsagePercent: 41
	},
	diskUsages: {
		disks: [
			{
				device: '/dev/nvme0n1p2',
				path: '/',
				blocksSize: 4096,
				blocks: 244_140_625,
				blocksFree: 151_367_187,
				blocksAvailable: 144_531_250,
				blocksUsed: 92_773_438,
				usagePercent: 38
			},
			{
				device: '/dev/nvme1n1p1',
				path: '/var/lib',
				blocksSize: 4096,
				blocks: 488_281_250,
				blocksFree: 322_265_625,
				blocksAvailable: 312_500_000,
				blocksUsed: 166_015_625,
				usagePercent: 34
			}
		]
	},
	hardwareHealth: {
		fans: [
			{ name: 'Fan 1A', rpm: 6240, status: 'OK' },
			{ name: 'Fan 1B', rpm: 6180, status: 'OK' },
			{ name: 'Fan 2A', rpm: 6300, status: 'OK' },
			{ name: 'Fan 2B', rpm: 6240, status: 'OK' },
			{ name: 'Fan 3A', rpm: 6120, status: 'OK' },
			{ name: 'Fan 3B', rpm: 6240, status: 'OK' }
		],
		powerSupplies: [
			{ name: 'PSU 1', watts: 412, status: 'OK' },
			{ name: 'PSU 2', watts: 408, status: 'OK' }
		],
		temperatures: [
			{ name: 'CPU 1', celsius: 52, status: 'OK' },
			{ name: 'CPU 2', celsius: 54, status: 'OK' },
			{ name: 'Inlet Ambient', celsius: 22, status: 'OK' },
			{ name: 'Exhaust', celsius: 38, status: 'OK' }
		],
		diskSmart: [
			{
				device: '/dev/nvme0n1',
				model: 'Samsung SSD 980 PRO 2TB',
				healthStatus: 'PASSED',
				temperatureCelsius: 41,
				wearoutPercent: 12,
				powerOnHours: 13_388
			},
			{
				device: '/dev/nvme1n1',
				model: 'Samsung SSD 980 PRO 2TB',
				healthStatus: 'PASSED',
				temperatureCelsius: 43,
				wearoutPercent: 14,
				powerOnHours: 13_388
			}
		],
		bmcFirmwareVersion: 'iDRAC9 7.10.50.10'
	},
	packageUpdates: {
		packageManager: 'apt',
		updates: [
			{ name: 'libc6', currentVersion: '2.39-0ubuntu8.3', availableVersion: '2.39-0ubuntu8.4', isSecurityUpdate: false },
			{ name: 'openssh-server', currentVersion: '1:9.6p1-3ubuntu13.5', availableVersion: '1:9.6p1-3ubuntu13.6', isSecurityUpdate: false },
			{ name: 'tzdata', currentVersion: '2024a-3ubuntu1.1', availableVersion: '2024b-0ubuntu0.24.04.1', isSecurityUpdate: false }
		]
	},
	failedServices: [],
	totalServices: 84,
	recentSshSessions: [
		{ user: 'alex', sourceIp: '10.0.0.42', sourcePort: 51_842, action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(8) },
		{ user: 'alex', sourceIp: '10.0.0.42', sourcePort: 51_842, action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(2) },
		{ user: 'deploy', sourceIp: '10.0.2.11', sourcePort: 44_021, action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(3) }
	],
	telemetryLastUpdated: secondsAgo(12)
};

// Rich detail for db-primary — the Warning machine. Disk wearout at 84% is the
// story; it's what an operator would screenshot to make "we'd have caught this"
// land. Used as the secondary hero shot if needed.
const dbPrimaryDetail: MachineDetailDto = {
	id: 3,
	name: 'db-primary',
	hostname: 'db-primary.prod.lan',
	isOnline: true,
	lastPing: secondsAgo(4),
	healthStatus: MachineHealthStatus.Warning,
	systemInfo: {
		hostname: 'db-primary.prod.lan',
		uuid: '00000000-0000-1000-8000-7c83fec6f4a4',
		cpuType: 'x86_64',
		cpuBrand: 'AMD EPYC 7763 64-Core Processor',
		cpuPhysicalCores: 64,
		cpuLogicalCores: 128,
		physicalMemory: 549_755_813_888,
		hardwareVendor: 'Supermicro',
		hardwareModel: 'AS-2024US-TRT',
		hardwareVersion: 'Rev 1.02',
		hardwareSerial: 'SM-2024US-A102',
		uptimeSeconds: 1_204_581,
		biosVersion: '2.7c',
		ipAddresses: ['10.0.1.21', 'fe80::3640:e3ff:fea8:b21d']
	},
	osVersion: {
		name: 'Ubuntu',
		version: '24.04.2 LTS',
		platform: 'linux',
		arch: 'x86_64',
		build: '6.8.0-51-generic'
	},
	cpuUsage: { cpuUsagePercent: 67 },
	memoryUsage: {
		memoryTotal: 549_755_813_888,
		memoryUsed: 428_809_534_833,
		memoryUsagePercent: 78
	},
	diskUsages: {
		disks: [
			{
				device: '/dev/nvme0n1p1',
				path: '/',
				blocksSize: 4096,
				blocks: 244_140_625,
				blocksFree: 195_312_500,
				blocksAvailable: 188_476_562,
				blocksUsed: 48_828_125,
				usagePercent: 20
			},
			{
				device: '/dev/md0',
				path: '/var/lib/postgresql',
				blocksSize: 4096,
				blocks: 3_906_250_000,
				blocksFree: 625_000_000,
				blocksAvailable: 562_500_000,
				blocksUsed: 3_281_250_000,
				usagePercent: 84
			}
		]
	},
	hardwareHealth: {
		fans: [
			{ name: 'FAN1', rpm: 8_400, status: 'OK' },
			{ name: 'FAN2', rpm: 8_280, status: 'OK' },
			{ name: 'FAN3', rpm: 8_460, status: 'OK' },
			{ name: 'FAN4', rpm: 8_340, status: 'OK' }
		],
		powerSupplies: [
			{ name: 'PSU1', watts: 612, status: 'OK' },
			{ name: 'PSU2', watts: 604, status: 'OK' }
		],
		temperatures: [
			{ name: 'CPU 1', celsius: 64, status: 'OK' },
			{ name: 'CPU 2', celsius: 67, status: 'OK' },
			{ name: 'Ambient', celsius: 24, status: 'OK' }
		],
		diskSmart: [
			{
				device: '/dev/nvme0n1',
				model: 'Intel SSD D7-P5520 3.84TB',
				healthStatus: 'PASSED',
				temperatureCelsius: 46,
				wearoutPercent: 38,
				powerOnHours: 22_104
			},
			{
				device: '/dev/nvme1n1',
				model: 'Intel SSD D7-P5520 3.84TB',
				healthStatus: 'PRE-FAIL',
				temperatureCelsius: 51,
				wearoutPercent: 84,
				powerOnHours: 22_104
			}
		],
		bmcFirmwareVersion: 'Supermicro X12 BMC 1.74.16'
	},
	packageUpdates: {
		packageManager: 'apt',
		updates: [
			{ name: 'postgresql-16', currentVersion: '16.4-1.pgdg24.04+1', availableVersion: '16.5-1.pgdg24.04+1', isSecurityUpdate: true },
			{ name: 'libpq5', currentVersion: '16.4-1.pgdg24.04+1', availableVersion: '16.5-1.pgdg24.04+1', isSecurityUpdate: true }
		]
	},
	failedServices: [],
	totalServices: 92,
	recentSshSessions: [
		{ user: 'alex', sourceIp: '10.0.0.42', sourcePort: 52_104, action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(1) },
		{ user: 'postgres-ops', sourceIp: '10.0.0.50', sourcePort: 38_902, action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(7) }
	],
	telemetryLastUpdated: secondsAgo(4)
};

// Build the detail map: rich entries for ID 1 and 3, sparse for everything else.
const detailMap = new Map<number, MachineDetailDto>();
detailMap.set(1, web01Detail);
detailMap.set(3, dbPrimaryDetail);
for (const machine of baseMachines) {
	if (detailMap.has(machine.id)) {
		continue;
	}
	const overview = baseMachines.find((m) => m.id === machine.id);
	const fallbackHealth = overview?.isOnline ? MachineHealthStatus.Healthy : MachineHealthStatus.Offline;
	detailMap.set(machine.id, sparseDetail(machine, fallbackHealth));
}

export const mockMachineDetailById: ReadonlyMap<number, MachineDetailDto> = detailMap;

// Authorized SSH keys shown on the machine-detail page (Authorized Keys tab).
// Real-looking fingerprints; harmless since they're truncated and not actual keys.
export const mockMachineAuthorizedKeys: MachineAuthorizedKeyDto[] = [
	{
		id: 1,
		signingKeyId: 1,
		label: 'Alex (laptop)',
		fingerprint: 'SHA256:p7N+I1xGfQjK4WLp3RDZ8YjVa6dHnQrCk0sFmTuLvWA',
		ownerUsername: 'alex',
		authorizedAt: daysAgo(189),
		authorizedByUsername: 'alex',
		revokedAt: null,
		isActive: true
	},
	{
		id: 2,
		signingKeyId: 2,
		label: 'Deploy bot',
		fingerprint: 'SHA256:K9zM4LhVqPx2NjB1WfCdYrTaQpEoUnSiGmHkLvIcBxE',
		ownerUsername: 'deploy',
		authorizedAt: daysAgo(154),
		authorizedByUsername: 'alex',
		revokedAt: null,
		isActive: true
	},
	{
		id: 3,
		signingKeyId: 3,
		label: 'On-call rotation',
		fingerprint: 'SHA256:L3xQ2bRkVpW9DyN6JhSfAcEoTuGmIlPnOqVrZbXcKdF',
		ownerUsername: 'oncall',
		authorizedAt: daysAgo(98),
		authorizedByUsername: 'alex',
		revokedAt: null,
		isActive: true
	}
];
