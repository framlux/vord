// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type { FleetSshSessionDto, PaginatedResponse } from '../types';
import { hoursAgo, minutesAgo } from './time';

// Fleet-wide SSH activity. Mix of operators (humans), service accounts (deploy,
// ansible, postgres-ops), and one failed login from an unrecognized IP — the
// kind of thing the marketing page wants to imply you'd catch.
const sshSessions: FleetSshSessionDto[] = [
	{ machineId: 3, machineName: 'db-primary', user: 'alex', sourceIp: '10.0.0.42', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(4) },
	{ machineId: 1, machineName: 'web-01', user: 'alex', sourceIp: '10.0.0.42', action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(7) },
	{ machineId: 1, machineName: 'web-01', user: 'alex', sourceIp: '10.0.0.42', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(12) },
	{ machineId: 16, machineName: 'gpu-trainer-01', user: 'ml-train', sourceIp: '10.0.4.10', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(18) },
	{ machineId: 2, machineName: 'web-02', user: 'deploy', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(28) },
	{ machineId: 1, machineName: 'web-01', user: 'deploy', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(29) },
	{ machineId: 2, machineName: 'web-02', user: 'deploy', sourceIp: '10.0.2.11', action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(31) },
	{ machineId: 1, machineName: 'web-01', user: 'deploy', sourceIp: '10.0.2.11', action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(32) },
	{ machineId: 7, machineName: 'hetzner-fsn1-42', user: 'ansible', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(41) },
	{ machineId: 8, machineName: 'hetzner-fsn1-43', user: 'ansible', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: minutesAgo(42) },
	{ machineId: 9, machineName: 'ovh-bhs5-12', user: 'root', sourceIp: '198.51.100.34', action: 'failed', authMethod: 'password', timestamp: minutesAgo(47) },
	{ machineId: 7, machineName: 'hetzner-fsn1-42', user: 'ansible', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(49) },
	{ machineId: 8, machineName: 'hetzner-fsn1-43', user: 'ansible', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: minutesAgo(50) },
	{ machineId: 3, machineName: 'db-primary', user: 'postgres-ops', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(1) },
	{ machineId: 3, machineName: 'db-primary', user: 'postgres-ops', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(1) },
	{ machineId: 4, machineName: 'db-replica-1', user: 'postgres-ops', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(2) },
	{ machineId: 12, machineName: 'ci-runner-02', user: 'ci', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(2) },
	{ machineId: 11, machineName: 'ci-runner-01', user: 'ci', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(3) },
	{ machineId: 5, machineName: 'cache-01', user: 'alex', sourceIp: '203.0.113.91', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(3) },
	{ machineId: 9, machineName: 'ovh-bhs5-12', user: 'root', sourceIp: '198.51.100.34', action: 'failed', authMethod: 'password', timestamp: hoursAgo(4) },
	{ machineId: 5, machineName: 'cache-01', user: 'alex', sourceIp: '203.0.113.91', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(4) },
	{ machineId: 10, machineName: 'ovh-bhs5-13', user: 'ansible', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(5) },
	{ machineId: 10, machineName: 'ovh-bhs5-13', user: 'ansible', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(5) },
	{ machineId: 13, machineName: 'vm-monitor', user: 'oncall', sourceIp: '10.0.0.42', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(6) },
	{ machineId: 13, machineName: 'vm-monitor', user: 'oncall', sourceIp: '10.0.0.42', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(6) },
	{ machineId: 6, machineName: 'cache-02', user: 'alex', sourceIp: '203.0.113.91', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(7) },
	{ machineId: 1, machineName: 'web-01', user: 'deploy', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(8) },
	{ machineId: 2, machineName: 'web-02', user: 'deploy', sourceIp: '10.0.2.11', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(8) },
	{ machineId: 6, machineName: 'cache-02', user: 'alex', sourceIp: '203.0.113.91', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(8) },
	{ machineId: 1, machineName: 'web-01', user: 'deploy', sourceIp: '10.0.2.11', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(9) },
	{ machineId: 2, machineName: 'web-02', user: 'deploy', sourceIp: '10.0.2.11', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(9) },
	{ machineId: 16, machineName: 'gpu-trainer-01', user: 'ml-train', sourceIp: '10.0.4.10', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(11) },
	{ machineId: 14, machineName: 'vm-logs', user: 'ansible', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(12) },
	{ machineId: 14, machineName: 'vm-logs', user: 'ansible', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(12) },
	{ machineId: 15, machineName: 'vm-gateway', user: 'ansible', sourceIp: '10.0.0.50', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(13) },
	{ machineId: 15, machineName: 'vm-gateway', user: 'ansible', sourceIp: '10.0.0.50', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(13) },
	{ machineId: 18, machineName: 'dev-shared', user: 'jordan', sourceIp: '10.0.0.71', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(15) },
	{ machineId: 18, machineName: 'dev-shared', user: 'jordan', sourceIp: '10.0.0.71', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(16) },
	{ machineId: 3, machineName: 'db-primary', user: 'alex', sourceIp: '203.0.113.91', action: 'connect', authMethod: 'publickey', timestamp: hoursAgo(18) },
	{ machineId: 3, machineName: 'db-primary', user: 'alex', sourceIp: '203.0.113.91', action: 'disconnect', authMethod: 'publickey', timestamp: hoursAgo(19) }
];

export const mockFleetSshSessions: PaginatedResponse<FleetSshSessionDto> = {
	items: sshSessions,
	page: 1,
	pageSize: 50,
	totalCount: sshSessions.length,
	totalPages: 1,
	hasNextPage: false,
	hasPreviousPage: false
};
