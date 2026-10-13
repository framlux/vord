// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

// Response wrappers
export interface ApiResponse<T> {
	success: boolean;
	data: T | null;
	message: string | null;
	errors: string[] | null;
}

export interface PaginatedResponse<T> {
	items: T[];
	page: number;
	pageSize: number;
	totalCount: number;
	totalPages: number;
	hasNextPage: boolean;
	hasPreviousPage: boolean;
}

// Enums
export enum OperatingSystem {
	Unknown = 0,
	Windows = 1,
	MacOS = 2,
	Ubuntu = 3,
	Fedora = 4,
	RedHat = 5,
	Debian = 6
}

export enum MachineType {
	Unknown = 0,
	Desktop = 1,
	Laptop = 2,
	BareMetalServer = 3,
	VirtualMachine = 4
}

export enum UserAccountRole {
	None = 0,
	TenantAdmin = 1,
	MachineAdmin = 2,
	Viewer = 3
}

// User DTOs
export interface UserDto {
	id: number;
	name: string;
	email: string;
	avatar: string;
	isGlobalAdmin: boolean;
	uniqueId: string;
	needsOnboarding: boolean;
	tenants: UserTenantDto[];
	activeTenantId: number | null;
	csrfToken?: string | null;
	// Optional because a freshly deployed web container can briefly talk to an older api-server
	// that does not send it. An absent value must be read as the hosted product.
	deployment?: { selfHosted: boolean };
}

export interface UserTenantDto {
	tenantId: number;
	tenantName: string;
	role: string;
}

export interface UserAccountDto {
	id: number;
	username: string;
	externalId: string;
	isActive: boolean;
	isGlobalAdmin: boolean;
	createdAt: string;
	tenants: UserTenantDto[];
}

// Tenant DTOs
export interface TenantDto {
	id: number;
	name: string;
	logoUrl: string;
	isActive: boolean;
}

// The machines a filter matches, as ids, for building a selection without transferring the
// machines themselves. `truncated` means `ids` is a prefix of the match rather than all of it.
export interface MachineIdSelectionDto {
	ids: number[];
	totalCount: number;
	truncated: boolean;
}

// Machine DTOs
export interface MachineDto {
	id: number;
	name: string;
	description: string | null;
	location: string | null;
	hostname: string;
	operatingSystem: OperatingSystem;
	machineType: MachineType;
	serialNumber: string;
	assetTag: string | null;
	isOnline: boolean;
	lastPing: string | null;
	registeredOn: string;
	isDeleted: boolean;
	commandsEnabled: boolean;
	/** Past the tenant's host limit. isOnline still reflects the heartbeat, so check this first. */
	isPaused: boolean;
}

export interface MachineStatusDto {
	isOnline: boolean;
	lastPing: string | null;
	commandsEnabled: boolean;
	healthStatus: MachineHealthStatus;
	isPaused: boolean;
}

// Dashboard (legacy)
export interface DashboardSummaryDto {
	totalMachines: number;
	onlineMachines: number;
	pausedMachines: number;
	pendingApprovals: number;
}

// Fleet overview
export enum MachineHealthStatus {
	Healthy = 0,
	Warning = 1,
	Critical = 2,
	Offline = 3
}

export interface FleetOverviewDto {
	summary: FleetSummaryDto;
	machines: FleetMachineDto[];
}

export interface PaginatedFleetOverviewDto {
	summary: FleetSummaryDto;
	machines: FleetMachineDto[];
	page: number;
	pageSize: number;
	totalCount: number;
	totalPages: number;
}

export interface FleetSummaryDto {
	totalMachines: number;
	onlineMachines: number;
	offlineCount: number;
	warningCount: number;
	criticalCount: number;
	/** Hosts past the plan's limit; they are in no health bucket and not counted as online. */
	pausedCount: number;
	securityUpdates: number;
}

export interface FleetMachineDto {
	id: number;
	name: string;
	hostname: string | null;
	ipAddress: string | null;
	hardwareModel: string | null;
	healthStatus: MachineHealthStatus;
	cpuUsagePercent: number | null;
	memoryUsagePercent: number | null;
	maxDiskUsagePercent: number | null;
	hasDiskHealthIssue: boolean;
	hasHardwareIssue: boolean;
	isOnline: boolean;
	isPaused: boolean;
	lastPing: string | null;
	pendingUpdates: number;
	securityUpdates: number;
	failedServices: number;
	totalServices: number;
}

// Machine detail
export interface MachineDetailDto {
	id: number;
	name: string;
	hostname: string | null;
	isOnline: boolean;
	lastPing: string | null;
	healthStatus: MachineHealthStatus;
	isPaused: boolean;
	/** The host's 1-based position in Id order among the tenant's registered hosts. */
	activeRank: number | null;
	systemInfo: SystemInfoDto | null;
	osVersion: OsVersionDto | null;
	cpuUsage: CpuUsageDto | null;
	memoryUsage: MemoryUsageDto | null;
	memoryInfo: MemoryInfoDto | null;
	/** Server receipt time of memoryInfo; the lookup window is seven days, so it can be stale. */
	memoryInfoReceivedAt: string | null;
	diskUsages: DiskUsageDto | null;
	hardwareHealth: HardwareHealthDto | null;
	packageUpdates: PackageUpdatesDto | null;
	failedServices: ServiceEntryDto[];
	totalServices: number;
	recentSshSessions: SshSessionDto[];
	telemetryLastUpdated: string | null;
	/** The running agent's build version, or null when the agent has not reported one. */
	agentVersion: string | null;
	/** What the machine reported about Kubernetes and its place in a cluster, or null when it has neither. */
	kubernetes: MachineKubernetesDto | null;
}

export interface SystemInfoDto {
	hostname: string;
	uuid: string;
	cpuType: string;
	cpuBrand: string;
	cpuPhysicalCores: number;
	cpuLogicalCores: number;
	physicalMemory: number;
	hardwareVendor: string;
	hardwareModel: string;
	hardwareVersion: string;
	hardwareSerial: string;
	uptimeSeconds: number;
	biosVersion: string;
	ipAddresses: string[];
}

export interface OsVersionDto {
	name: string;
	version: string;
	platform: string;
	arch: string;
	build: string;
}

export interface CpuUsageDto {
	cpuUsagePercent: number;
	/** The eight jiffy buckets below decompose cpuUsagePercent; they are not additional load. */
	userTime: number;
	systemTime: number;
	niceTime: number;
	idleTime: number;
	iowaitTime: number;
	irqTime: number;
	softirqTime: number;
	stealTime: number;
}

/** Memory and swap totals (telemetry type 4), reported on the agent's 15-minute slow tick. */
export interface MemoryInfoDto {
	memoryTotal: number;
	memoryFree: number;
	memoryAvailable: number;
	swapTotal: number;
	swapFree: number;
}

export interface MemoryUsageDto {
	memoryTotal: number;
	memoryUsed: number;
	memoryUsagePercent: number;
}

export interface DiskUsageDto {
	disks: DiskUsageEntryDto[];
}

export interface DiskUsageEntryDto {
	device: string;
	path: string;
	blocksSize: number;
	blocks: number;
	blocksFree: number;
	blocksAvailable: number;
	blocksUsed: number;
	usagePercent: number;
}

export interface HardwareHealthDto {
	fans: FanReadingDto[];
	powerSupplies: PowerSupplyReadingDto[];
	temperatures: TemperatureReadingDto[];
	diskSmart: DiskSmartReadingDto[];
	bmcFirmwareVersion: string;
}

export interface FanReadingDto {
	name: string;
	rpm: number;
	status: string;
}

export interface PowerSupplyReadingDto {
	name: string;
	watts: number;
	status: string;
}

export interface TemperatureReadingDto {
	name: string;
	celsius: number;
	status: string;
}

export interface DiskSmartReadingDto {
	device: string;
	model: string;
	healthStatus: string;
	temperatureCelsius: number;
	wearoutPercent: number;
	powerOnHours: number;
}

export interface PackageUpdatesDto {
	packageManager: string;
	updates: PackageUpdateDto[];
}

export interface PackageUpdateDto {
	name: string;
	currentVersion: string;
	availableVersion: string;
	isSecurityUpdate: boolean;
}

export interface ServiceEntryDto {
	unit: string;
	loadState: string;
	activeState: string;
	subState: string;
	description: string;
}

export interface SshSessionDto {
	user: string;
	sourceIp: string;
	sourcePort: number;
	action: string;
	authMethod: string;
	timestamp: string;
}

// Admin
export interface ServerSettingsDto {
	settings: SettingEntry[];
}

export interface SettingEntry {
	key: number;
	name: string;
	description: string;
	value: string;
	min: number | null;
	max: number | null;
}

export enum SubscriptionTier {
	None = 0,
	Free = 1,
	Pro = 2,
	Team = 3,
	Enterprise = 4
}

export enum SubscriptionStatus {
	None = 0,
	Active = 1,
	PastDue = 2,
	Canceled = 3
}

export interface SubscriptionDto {
	tier: string;
	status: string;
	machineLimit: number;
	machineCount: number;
	retentionDays: number;
	currentPeriodEnd: string | null;
	cancelAtPeriodEnd: boolean;
	billingInterval: string | null;
	pendingAction: string | null;
	alertRuleLimit: number;
	alertRuleCount: number;
	webhookLimit: number;
	webhookCount: number;
	memberLimit: number;
	memberCount: number;
	/** Hosts registered beyond machineLimit; they are paused, not removed. */
	pausedMachineCount: number;
	/** What the tier bills for, or null on tiers that are not billed per host. */
	billableMachineCount: number | null;
	/** True when an override or agreement, not the tier, sets machineLimit. */
	machineLimitIsCustom: boolean;
}

export interface UpcomingInvoiceDto {
	hasInvoice: boolean;
	amountDueCents: number;
	currency: string;
	periodStart: string | null;
	periodEnd: string | null;
	nextPaymentAttempt: string | null;
	unitAmountCents: number;
	discountAmountCents: number;
	lines: LineItemDto[];
}

export interface LineItemDto {
	description: string;
	amountCents: number;
	quantity: number;
	periodStart: string | null;
	periodEnd: string | null;
	proration: boolean;
}

export interface CatalogItemDto {
	tier: string;
	interval: string | null;
	unitAmountCents: number;
	currency: string;
}

export interface InvoiceDto {
	id: string;
	amountCents: number;
	currency: string;
	status: string;
	created: string;
	periodStart: string | null;
	periodEnd: string | null;
	hostedInvoiceUrl: string;
	invoicePdfUrl: string;
}

export interface UsagePointDto {
	month: string;
	machineCount: number;
	invoiceAmountCents: number;
}

// Invitations
export enum InvitationStatus {
	None = 0,
	Pending = 1,
	Accepted = 2,
	Revoked = 3,
	Expired = 4
}

export interface InvitationDto {
	id: number;
	email: string;
	token: string;
	acceptUrl: string;
	expiresAt: string;
	status: string;
}

export interface InvitationListDto {
	id: number;
	email: string;
	status: string;
	createdAt: string;
	expiresAt: string;
	role: string;
}

export interface InvitationDetailDto {
	tenantName: string;
	inviterEmail: string;
	email: string;
	expiresAt: string;
	status: string;
}

export interface MemberDto {
	userId: number;
	email: string;
	role: string;
	joinedAt: string;
}

// Registration Tokens
export interface RegistrationTokenDto {
	id: number;
	name: string;
	token: string | null;
	createdAt: string;
	isRevoked: boolean;
}

export interface CreateRegistrationTokenRequest {
	name: string;
}

// Fleet SSH Sessions
export interface FleetSshSessionDto {
	machineId: number;
	machineName: string;
	user: string;
	sourceIp: string;
	action: string;
	authMethod: string;
	timestamp: string;
}

// Alert Rules
export interface AlertRuleMachineDto {
	id: number;
	name: string;
}

export interface AlertRuleDto {
	id: number;
	name: string;
	description: string | null;
	metric: string;
	operator: string;
	threshold: number;
	durationMinutes: number;
	severity: string;
	isEnabled: boolean;
	notifyEmail: boolean;
	notifyWebhook: boolean;
	isCustom: boolean;
	machineIds: number[];
	machines: AlertRuleMachineDto[];
}

export interface AlertEventDto {
	id: number;
	ruleName: string;
	machineId: number;
	machineName: string;
	severity: string;
	message: string;
	status: string;
	triggeredAt: string;
	acknowledgedAt: string | null;
	acknowledgedByUserId: number | null;
	resolvedAt: string | null;
}

export interface IntegrationEndpointDto {
	id: number;
	provider: string;
	name: string;
	isEnabled: boolean;
	createdAt: string;
	secret?: string | null;
}

export interface IntegrationTestResultDto {
	success: boolean;
	statusCode: number | null;
	message: string;
}

export interface IntegrationProviderDto {
	provider: string;
	displayName: string;
	description: string;
	configFields: IntegrationConfigField[];
}

export interface IntegrationConfigField {
	key: string;
	label: string;
	type: string;
	placeholder: string;
	helpText: string;
	helpUrl?: string;
}

export interface CreateAlertRuleRequest {
	name: string;
	description?: string;
	metric: string;
	operator: string;
	threshold: number;
	durationMinutes: number;
	severity: string;
	notifyEmail: boolean;
	notifyWebhook: boolean;
	machineIds: number[];
}

export interface UpdateAlertRuleRequest {
	name: string;
	description?: string;
	metric: string;
	threshold: number;
	durationMinutes: number;
	severity: string;
	isEnabled: boolean;
	notifyEmail: boolean;
	notifyWebhook: boolean;
	machineIds: number[];
	/**
	 * The machines the caller was choosing from. Only assignments named here may be removed, so a
	 * picker that rendered one page of a larger fleet cannot unassign the machines it never drew.
	 */
	visibleMachineIds: number[];
}

export interface UpdateAlertRuleEnabledRequest {
	isEnabled: boolean;
}

export interface UpdateAlertRuleMachinesRequest {
	machineIds: number[];
	/**
	 * The machines the caller was choosing from. Only assignments named here may be removed, so a
	 * picker that rendered one page of a larger fleet cannot unassign the machines it never drew.
	 */
	visibleMachineIds: number[];
}

export interface CreateIntegrationRequest {
	provider: string;
	name?: string;
	configuration: Record<string, string>;
}

export interface UpdateIntegrationRequest {
	name?: string;
	isEnabled?: boolean;
	configuration?: Record<string, string>;
}

// Machine Update
export interface UpdateMachineRequest {
	name: string;
	description?: string | null;
	location?: string | null;
}

// Machine Authorized Keys
export interface MachineAuthorizedKeyDto {
	id: number;
	signingKeyId: number;
	label: string;
	fingerprint: string;
	ownerUsername: string;
	authorizedAt: string;
	authorizedByUsername: string;
	revokedAt: string | null;
	isActive: boolean;
}

// Signing Keys
export interface SigningKeyDto {
	id: number;
	label: string;
	publicKey: string;
	fingerprint: string;
	createdAt: string;
	revokedAt: string | null;
}

export interface SigningKeyListResponse {
	keys: SigningKeyDto[];
	activeCount: number;
	maxKeys: number;
}

export interface RegisterSigningKeyRequest {
	label: string;
	publicKey: string;
}

// Remote Commands
export interface CommandDto {
	id: number;
	commandId: string;
	machineId: number;
	commandType: string;
	status: string;
	createdAt: string;
	expiresAt: string;
	deliveredAt: string | null;
	completedAt: string | null;
	exitCode: number | null;
	resultMessage: string | null;
}

export interface SendCommandRequest {
	commandId: string;
	machineId: number;
	signingKeyId: number;
	commandType: string;
	/** JSON-encoded parameters, or null for a command that takes none. Never an empty string. */
	params?: string | null;
	nonce: string;
	signature: string;
	canonicalPayload: string;
	timestamp: string;
	expiresAt: string;
}

// Audit Log
export interface AuditLogEntryDto {
	id: number;
	userEmail: string | null;
	userId: number | null;
	machineId: number | null;
	action: string;
	resourceType: string;
	resourceId: string | null;
	details: string | null;
	ipAddress: string | null;
	timestamp: string;
}

// Machine Search
export interface MachineSearchParams {
	page?: number;
	pageSize?: number;
	search?: string;
	healthStatus?: string;
	/** 'true' restricts the search to paused hosts. Paused hosts are in no health bucket. */
	paused?: string;
	os?: string;
	type?: string;
	cpuMin?: number;
	cpuMax?: number;
	memoryMin?: number;
	memoryMax?: number;
	diskMin?: number;
	diskMax?: number;
	pendingUpdatesMin?: number;
	securityUpdatesMin?: number;
	failedServicesMin?: number;
	hasDiskHealthIssue?: boolean;
	hasHardwareIssue?: boolean;
	lastSeenAfter?: string;
	lastSeenBefore?: string;
	sortBy?: string;
	sortDir?: string;
}

// History / Trends
export interface HistoryPoint {
	timestamp: string;
	value: number;
}

export interface HistoryStats {
	min: number;
	avg: number;
	max: number;
	p95: number;
}

export interface HistoryResponse {
	points: HistoryPoint[];
	stats: HistoryStats;
	bucketSeconds: number;
	rawPointCount: number;
}

export interface DiskSeries {
	device: string;
	mountPoint: string;
	points: HistoryPoint[];
	stats: HistoryStats;
}

export interface DiskHistoryResponse {
	series: DiskSeries[];
	bucketSeconds: number;
	rawPointCount: number;
}

export interface ServiceHistoryPoint {
	timestamp: string;
	failedCount: number;
	totalCount: number;
}

export interface ServiceHistoryResponse {
	points: ServiceHistoryPoint[];
	stats: HistoryStats;
	bucketSeconds: number;
	rawPointCount: number;
}

export interface SshEvent {
	timestamp: string;
	user: string;
	sourceIp: string;
	sourcePort: number;
	action: string;
	authMethod: string;
}

export interface SshHistoryResponse {
	events: SshEvent[];
	totalEvents: number;
}

/** How a machine relates to the Kubernetes clusters of its tenant. */
export type KubernetesGrouping = 'grouped' | 'ambiguous' | 'unknown' | 'ungrouped';

/**
 * How much of a cluster Vord can see through its cluster reporter: off while no machine is enrolled,
 * error while the reporter reports one of the failures in KubernetesClusterSummaryDto.reporterError,
 * role_outdated while the reporter's permissions predate what Vord needs, stale once the latest
 * snapshot is older than the staleness threshold, and active otherwise.
 */
export type KubernetesReportingState = 'off' | 'error' | 'role_outdated' | 'stale' | 'active';

export interface KubernetesClusterSummaryDto {
	id: number;
	/** The name to show: the operator-chosen name when there is one, else the derived default. */
	name: string;
	/** The operator-chosen name as stored, or null while the cluster uses its derived name. */
	displayName: string | null;
	/** k3s, rke2, kubeadm, k0s, microk8s or unknown. */
	distro: string;
	nodeCount: number;
	managedNodeCount: number;
	unmanagedNodeCount: number;
	kubeletVersions: string[];
	readyCount: number;
	notReadyCount: number;
	unknownCount: number;
	reportingState: KubernetesReportingState;
	/**
	 * The machine that sent the cluster's most recent accepted snapshot (the last sender), or null when none has.
	 * It names the last sender whether or not that snapshot is still fresh; a candidate's isActiveReporter
	 * additionally requires it to be fresh, and neither proves the machine holds the Lease.
	 */
	activeReporterMachineId: number | null;
	activeReporterMachineName: string | null;
	/** The Kubernetes server version the latest snapshot reported, or null when there has been none. */
	clusterVersion: string | null;
	/**
	 * The code that puts the cluster in the error state: credentials_rejected, tls_ca_changed,
	 * api_unreachable, namespace_terminating or, from a candidate's own report, state_copied. Null in
	 * every other state.
	 */
	reporterError: string | null;
	/** True when the latest snapshot listed only some of the cluster's nodes to fit its size cap. */
	truncated: boolean;
	lastSnapshotReceivedAt: string | null;
	/** When the node values date from, once they are stale. Null otherwise. */
	staleSince: string | null;
	firstSeenAt: string;
	lastSeenAt: string;
}

/** A node condition that is not in its healthy state, such as MemoryPressure being True. */
export interface KubernetesNodeConditionDto {
	/** MemoryPressure, DiskPressure, PIDPressure or NetworkUnavailable. */
	type: string;
	/** True or Unknown: a condition that is False is not reported. */
	status: string;
	lastTransitionTime: string;
}

export interface KubernetesNodeDto {
	id: number;
	nodeName: string;
	controlPlane: boolean;
	kubeletVersion: string | null;
	/** The linked machine, or null while the node is not managed by Vord. */
	machineId: number | null;
	machineName: string | null;
	/** True while the node reports a CA fingerprint different from its cluster's. */
	fingerprintMismatch: boolean;
	/** When the node's own machine last reported it, or null if only another source has. */
	lastLocalReportAt: string | null;
	/** The roles the cluster reporter lists for the node; empty when it has not listed the node. */
	roles: string[];
	/** The Ready condition's status (True, False or Unknown), or null when no snapshot has listed the node. */
	readyStatus: string | null;
	readySince: string | null;
	/** The pressure and network conditions whose status is not False. */
	conditions: KubernetesNodeConditionDto[];
	/** True while the node is cordoned and takes no new pods. */
	unschedulable: boolean;
	unschedulableSince: string | null;
	containerRuntimeVersion: string | null;
	/** The pods scheduled on the node, or null when the snapshot did not count them. */
	podCount: number | null;
	podCapacity: number | null;
	/** When a cluster snapshot last listed the node, or null if only local reports have. */
	lastReporterSeenAt: string | null;
	/** True when the kubelet is more than three minor versions behind the cluster, or newer than it. */
	kubeletSkew: boolean;
}

/** A machine enrolled as a reporter candidate of a cluster. */
export interface KubernetesCandidateDto {
	machineId: number;
	machineName: string;
	/**
	 * True when this machine sent the cluster's most recent accepted snapshot and that snapshot is not stale.
	 * The server cannot see the Lease, so this is not proof the machine holds it now.
	 */
	isActiveReporter: boolean;
	/**
	 * The error the machine's own reporter last reported, as a bare code (credentials_rejected,
	 * tls_ca_changed or state_copied), or null when it reported none.
	 */
	reporterError: string | null;
	lastReportAt: string;
	online: boolean;
}

/** A machine that can be enrolled as a reporter, with what decides whether a command can be sent to it. */
export interface KubernetesEligibleMachineDto {
	machineId: number;
	machineName: string;
	online: boolean;
	/** True while the machine is paused, which makes the server refuse its commands with a conflict. */
	isPaused: boolean;
	/** Whether the agent reported the remote commands capability. Null when its capabilities could not be read. */
	remoteCommandsEnabled: boolean | null;
	/** True when the machine's latest report says it is already enrolled as a reporter. */
	enrolled: boolean;
	/** The ids of the signing keys authorized to send commands to the machine. */
	authorizedSigningKeyIds: number[];
}

export interface KubernetesClusterDetailDto extends KubernetesClusterSummaryDto {
	caFingerprint: string;
	/** True when any node reports a CA fingerprint different from the cluster's. */
	hasFingerprintMismatch: boolean;
	kubeSystemUid: string | null;
	nodes: KubernetesNodeDto[];
	candidates: KubernetesCandidateDto[];
	eligibleMachines: KubernetesEligibleMachineDto[];
}

export interface MachineKubernetesLocalDto {
	distro: string;
	/** Empty when no kubelet was found on the host. */
	kubeletVersion: string;
	/** Null when the host runs no kubelet, because it is then not a node and has no node name to show. */
	nodeName: string | null;
	/** Where the node name came from: certificate or hostname. Null when nodeName is. */
	nodeNameSource: string | null;
	hasKubelet: boolean;
	controlPlane: boolean;
	adminCredentialPresent: boolean;
	reporterEnrolled: boolean;
}

/** A machine's state as a reporter candidate. */
export interface MachineKubernetesCandidateDto {
	clusterId: number;
	/**
	 * True when this machine sent the cluster's most recent accepted snapshot and that snapshot is not stale.
	 * The server cannot see the Lease, so this is not proof the machine holds it now.
	 */
	isActiveReporter: boolean;
	reporterError: string | null;
	lastReportAt: string;
}

export interface MachineKubernetesDto {
	/** The machine's latest local report from the past seven days, or null when there is none. */
	local: MachineKubernetesLocalDto | null;
	/** The node the machine is linked to, or null when it is not a known node. */
	node: KubernetesNodeDto | null;
	/** The cluster the machine belongs to, or null when it has none or the choice is ambiguous. */
	clusterId: number | null;
	clusterName: string | null;
	grouping: KubernetesGrouping;
	/** The machine's state as a reporter candidate, or null when it is not enrolled as one. */
	candidate: MachineKubernetesCandidateDto | null;
}
