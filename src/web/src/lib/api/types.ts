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
	RedHat = 5
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
}

export interface MachineStatusDto {
	isOnline: boolean;
	lastPing: string | null;
	commandsEnabled: boolean;
}

// Dashboard (legacy)
export interface DashboardSummaryDto {
	totalMachines: number;
	onlineMachines: number;
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
	systemInfo: SystemInfoDto | null;
	osVersion: OsVersionDto | null;
	cpuUsage: CpuUsageDto | null;
	memoryUsage: MemoryUsageDto | null;
	diskUsages: DiskUsageDto | null;
	hardwareHealth: HardwareHealthDto | null;
	packageUpdates: PackageUpdatesDto | null;
	failedServices: ServiceEntryDto[];
	totalServices: number;
	recentSshSessions: SshSessionDto[];
	telemetryLastUpdated: string | null;
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
	Team = 3
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
	machineLimit: number | null;
	machineCount: number;
	retentionDays: number;
	currentPeriodEnd: string | null;
	cancelAtPeriodEnd: boolean;
	pendingAction: string | null;
}

export interface UpcomingInvoiceDto {
	hasInvoice: boolean;
	amountDueCents: number;
	currency: string;
	periodStart: string | null;
	periodEnd: string | null;
	nextPaymentAttempt: string | null;
	unitAmountCents: number;
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

export interface WebhookEndpointDto {
	id: number;
	name: string;
	url: string;
	isEnabled: boolean;
	createdAt: string;
	secret?: string | null;
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
}

export interface UpdateAlertRuleRequest {
	name: string;
	description?: string;
	threshold: number;
	durationMinutes: number;
	severity: string;
	isEnabled: boolean;
	notifyEmail: boolean;
	notifyWebhook: boolean;
}

export interface CreateWebhookRequest {
	name: string;
	url: string;
}

export interface UpdateWebhookRequest {
	isEnabled: boolean;
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
	params?: string;
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

