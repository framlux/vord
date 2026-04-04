// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

import type {
	ApiResponse,
	PaginatedResponse,
	UserDto,
	DashboardSummaryDto,
	FleetOverviewDto,
	PaginatedFleetOverviewDto,
	MachineDetailDto,
	MachineDto,
	MachineStatusDto,
	MachineTelemetryDto,
	MachineCertificateDto,
	UserAccountDto,
	TenantDto,
	ServerSettingsDto,
	SubscriptionDto,
	InvitationDto,
	InvitationListDto,
	InvitationDetailDto,
	MemberDto,
	RegistrationTokenDto,
	CreateRegistrationTokenRequest,
	AuditLogEntryDto,
	FleetSshSessionDto,
	AlertRuleDto,
	AlertEventDto,
	WebhookEndpointDto,
	CreateAlertRuleRequest,
	UpdateAlertRuleRequest,
	CreateWebhookRequest,
	SigningKeyDto,
	SigningKeyListResponse,
	RegisterSigningKeyRequest,
	CommandDto,
	SendCommandRequest,
	UpcomingInvoiceDto,
	InvoiceDto,
	UsagePointDto,
	MachineSearchParams,
	FleetMachineDto
} from './types';
import { buildQueryString } from '$lib/utils/query';

export class ApiError extends Error {
	constructor(
		public status: number,
		message: string
	) {
		super(message);
		this.name = 'ApiError';
	}
}

export class ApiClient {
	private baseUrl: string;
	private fetchFn: typeof fetch;

	constructor(baseUrl: string, fetchFn: typeof fetch = fetch) {
		this.baseUrl = baseUrl.replace(/\/$/, '');
		this.fetchFn = fetchFn;
	}

	private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
		const url = `${this.baseUrl}${path}`;
		const headers: Record<string, string> = {
			Accept: 'application/json'
		};

		if (body !== undefined) {
			headers['Content-Type'] = 'application/json';
		}

		const response = await this.fetchFn(url, {
			method,
			headers,
			body: body !== undefined ? JSON.stringify(body) : undefined,
			credentials: 'include'
		});

		if (response.status === 401) {
			throw new ApiError(401, 'Unauthorized');
		}
		if (response.status === 403) {
			throw new ApiError(403, 'Forbidden');
		}
		if (response.status === 404) {
			throw new ApiError(404, 'Not found');
		}

		let json: unknown;
		try {
			json = await response.json();
		} catch {
			throw new ApiError(response.status, `Request failed with status ${response.status} (${response.statusText})`);
		}

		if (response.ok === false) {
			const apiResp = json as ApiResponse<unknown>;
			throw new ApiError(response.status, apiResp?.message ?? 'Request failed');
		}

		return json as T;
	}

	private async get<T>(path: string): Promise<T> {
		return this.request<T>('GET', path);
	}

	private async post<T>(path: string, body?: unknown): Promise<T> {
		return this.request<T>('POST', path, body);
	}

	private async put<T>(path: string, body?: unknown): Promise<T> {
		return this.request<T>('PUT', path, body);
	}

	private async del<T>(path: string): Promise<T> {
		return this.request<T>('DELETE', path);
	}

	// Unwrap ApiResponse
	private unwrap<T>(resp: ApiResponse<T>): T {
		if (resp.success === false || resp.data === null) {
			throw new ApiError(500, resp.message ?? 'Unknown error');
		}
		return resp.data;
	}

	// Auth
	async getMe(): Promise<UserDto> {
		const resp = await this.get<ApiResponse<UserDto>>('/api/v1/auth/me');
		return this.unwrap(resp);
	}

	async logout(): Promise<void> {
		await this.post<void>('/api/v1/logout');
	}

	async emailLookup(email: string): Promise<{ tenantId: number }> {
		const resp = await this.post<ApiResponse<{ tenantId: number }>>('/api/v1/auth/email-lookup', { email });

		return this.unwrap(resp);
	}

	// Subscription
	async getSubscription(): Promise<SubscriptionDto> {
		const resp = await this.get<ApiResponse<SubscriptionDto>>('/api/v1/billing/subscription');
		return this.unwrap(resp);
	}

	async cancelSubscription(): Promise<{ success: boolean; message: string }> {
		const resp = await this.post<ApiResponse<{ success: boolean; message: string }>>('/api/v1/billing/cancel');
		return this.unwrap(resp);
	}

	async downgradeSubscription(targetTier: string): Promise<{ success: boolean; message: string }> {
		const resp = await this.post<ApiResponse<{ success: boolean; message: string }>>('/api/v1/billing/downgrade', { targetTier });
		return this.unwrap(resp);
	}

	async resumeSubscription(): Promise<{ success: boolean; message: string }> {
		const resp = await this.post<ApiResponse<{ success: boolean; message: string }>>('/api/v1/billing/resume');
		return this.unwrap(resp);
	}

	async reactivateSubscription(): Promise<{ success: boolean; message: string }> {
		const resp = await this.post<ApiResponse<{ success: boolean; message: string }>>('/api/v1/billing/reactivate');
		return this.unwrap(resp);
	}

	async getUpcomingInvoice(): Promise<UpcomingInvoiceDto> {
		const resp = await this.get<ApiResponse<UpcomingInvoiceDto>>('/api/v1/billing/upcoming-invoice');
		return this.unwrap(resp);
	}

	async getInvoices(): Promise<InvoiceDto[]> {
		const resp = await this.get<ApiResponse<InvoiceDto[]>>('/api/v1/billing/invoices');
		return this.unwrap(resp);
	}

	async getUsageHistory(months: number = 6): Promise<UsagePointDto[]> {
		const resp = await this.get<ApiResponse<UsagePointDto[]>>(`/api/v1/billing/usage-history?months=${months}`);
		return this.unwrap(resp);
	}

	// Onboarding
	async createOrganization(data: { organizationName: string }): Promise<{ tenantId: number }> {
		const resp = await this.post<ApiResponse<{ tenantId: number }>>('/api/v1/onboarding/create-org', data);

		return this.unwrap(resp);
	}

	// Dashboard
	async getDashboardSummary(): Promise<DashboardSummaryDto> {
		const resp = await this.get<ApiResponse<DashboardSummaryDto>>('/api/v1/dashboard/summary');
		return this.unwrap(resp);
	}

	async getFleetOverview(params?: {
		page?: number;
		pageSize?: number;
		search?: string;
		status?: string;
		sortBy?: string;
		sortDir?: string;
	}): Promise<PaginatedFleetOverviewDto> {
		const qs = buildQueryString(params ?? {});
		const url = qs ? `/api/v1/dashboard/fleet?${qs}` : '/api/v1/dashboard/fleet';
		const resp = await this.get<ApiResponse<PaginatedFleetOverviewDto>>(url);

		return this.unwrap(resp);
	}

	async getMachineDetail(id: number): Promise<MachineDetailDto> {
		const resp = await this.get<ApiResponse<MachineDetailDto>>(`/api/v1/machines/${id}/detail`);
		return this.unwrap(resp);
	}

	// Machines
	async getMachines(params?: {
		page?: number;
		pageSize?: number;
		search?: string;
		os?: string;
		type?: string;
		status?: string;
		sortBy?: string;
		sortDir?: string;
	}): Promise<PaginatedResponse<MachineDto>> {
		const qs = buildQueryString(params ?? {});
		const resp = await this.get<ApiResponse<PaginatedResponse<MachineDto>>>(
			`/api/v1/machines${qs ? `?${qs}` : ''}`
		);
		return this.unwrap(resp);
	}

	async searchMachines(params: MachineSearchParams): Promise<PaginatedResponse<FleetMachineDto>> {
		const qs = buildQueryString(params);
		const resp = await this.get<ApiResponse<PaginatedResponse<FleetMachineDto>>>(
			`/api/v1/machines/search${qs ? `?${qs}` : ''}`
		);
		return this.unwrap(resp);
	}

	async getMachine(id: number): Promise<MachineDto> {
		const resp = await this.get<ApiResponse<MachineDto>>(`/api/v1/machines/${id}`);
		return this.unwrap(resp);
	}

	async getMachineStatus(id: number): Promise<MachineStatusDto> {
		const resp = await this.get<ApiResponse<MachineStatusDto>>(`/api/v1/machines/${id}/status`);
		return this.unwrap(resp);
	}

	async getMachineTelemetry(
		id: number,
		params?: { page?: number; pageSize?: number; type?: number }
	): Promise<PaginatedResponse<MachineTelemetryDto>> {
		const qs = buildQueryString(params ?? {});
		const resp = await this.get<ApiResponse<PaginatedResponse<MachineTelemetryDto>>>(
			`/api/v1/machines/${id}/telemetry${qs ? `?${qs}` : ''}`
		);
		return this.unwrap(resp);
	}

	async getMachineTelemetryLatest(id: number): Promise<MachineTelemetryDto[]> {
		const resp = await this.get<ApiResponse<MachineTelemetryDto[]>>(
			`/api/v1/machines/${id}/telemetry/latest`
		);
		return this.unwrap(resp);
	}

	async getMachineCertificates(id: number): Promise<MachineCertificateDto[]> {
		const resp = await this.get<ApiResponse<MachineCertificateDto[]>>(
			`/api/v1/machines/${id}/certificates`
		);
		return this.unwrap(resp);
	}

	async deleteMachine(id: number): Promise<void> {
		const resp = await this.del<ApiResponse<object>>(`/api/v1/machines/${id}`);
		this.unwrap(resp);
	}

	// Registration Tokens
	async getRegistrationTokens(): Promise<RegistrationTokenDto[]> {
		const resp = await this.get<ApiResponse<PaginatedResponse<RegistrationTokenDto>>>('/api/v1/tenants/registration-tokens');

		return this.unwrap(resp).items;
	}

	async createRegistrationToken(req: CreateRegistrationTokenRequest): Promise<RegistrationTokenDto> {
		const resp = await this.post<ApiResponse<RegistrationTokenDto>>('/api/v1/tenants/registration-tokens', req);

		return this.unwrap(resp);
	}

	async revokeRegistrationToken(id: number): Promise<void> {
		const resp = await this.del<ApiResponse<object>>(`/api/v1/tenants/registration-tokens/${id}`);
		this.unwrap(resp);
	}

	// Users
	async getUsers(): Promise<UserAccountDto[]> {
		const resp = await this.get<ApiResponse<UserAccountDto[]>>('/api/v1/users');
		return this.unwrap(resp);
	}

	async getUser(id: number): Promise<UserAccountDto> {
		const resp = await this.get<ApiResponse<UserAccountDto>>(`/api/v1/users/${id}`);
		return this.unwrap(resp);
	}

	async deactivateUser(id: number): Promise<void> {
		const resp = await this.post<ApiResponse<object>>(`/api/v1/users/${id}/deactivate`);
		this.unwrap(resp);
	}

	// Tenants
	async getTenants(): Promise<TenantDto[]> {
		const resp = await this.get<ApiResponse<TenantDto[]>>('/api/v1/tenants');
		return this.unwrap(resp);
	}

	async getTenant(id: number): Promise<TenantDto> {
		const resp = await this.get<ApiResponse<TenantDto>>(`/api/v1/tenants/${id}`);
		return this.unwrap(resp);
	}

	async createTenant(data: { name: string; logoUrl: string }): Promise<TenantDto> {
		const resp = await this.post<ApiResponse<TenantDto>>('/api/v1/tenants', data);
		return this.unwrap(resp);
	}

	// Admin
	async getAdminUsers(): Promise<UserAccountDto[]> {
		const resp = await this.get<ApiResponse<UserAccountDto[]>>('/api/v1/admin/users');
		return this.unwrap(resp);
	}

	async getAdminSettings(): Promise<ServerSettingsDto> {
		const resp = await this.get<ApiResponse<ServerSettingsDto>>('/api/v1/admin/settings');
		return this.unwrap(resp);
	}

	async updateAdminSettings(
		settings: { key: number; value: string }[]
	): Promise<ServerSettingsDto> {
		const resp = await this.put<ApiResponse<ServerSettingsDto>>('/api/v1/admin/settings', {
			settings
		});
		return this.unwrap(resp);
	}

	// Invitations
	async getInvitations(): Promise<InvitationListDto[]> {
		const resp = await this.get<ApiResponse<InvitationListDto[]>>('/api/v1/invitations');
		return this.unwrap(resp);
	}

	async createInvitation(email: string, role?: string): Promise<InvitationDto> {
		const resp = await this.post<ApiResponse<InvitationDto>>('/api/v1/invitations', { email, role });
		return this.unwrap(resp);
	}

	async changeMemberRole(userId: number, role: string): Promise<void> {
		const resp = await this.put<ApiResponse<object>>(`/api/v1/members/${userId}/role`, { role });
		this.unwrap(resp);
	}

	async revokeInvitation(id: number): Promise<void> {
		const resp = await this.post<ApiResponse<object>>(`/api/v1/invitations/${id}/revoke`);
		this.unwrap(resp);
	}

	async resendInvitation(id: number): Promise<InvitationDto> {
		const resp = await this.post<ApiResponse<InvitationDto>>(`/api/v1/invitations/${id}/resend`);
		return this.unwrap(resp);
	}

	async getInvitationByToken(token: string): Promise<InvitationDetailDto> {
		const resp = await this.get<ApiResponse<InvitationDetailDto>>(`/api/v1/invitations/by-token/${token}`);
		return this.unwrap(resp);
	}

	async acceptInvitation(token: string): Promise<{ tenantId: number }> {
		const resp = await this.post<ApiResponse<{ tenantId: number }>>(`/api/v1/invitations/${token}/accept`);
		return this.unwrap(resp);
	}

	// Members
	async getMembers(): Promise<MemberDto[]> {
		const resp = await this.get<ApiResponse<MemberDto[]>>('/api/v1/members');
		return this.unwrap(resp);
	}

	async removeMember(userId: number): Promise<void> {
		const resp = await this.post<ApiResponse<object>>(`/api/v1/members/${userId}/remove`);
		this.unwrap(resp);
	}

	// Audit Log
	async getAuditLog(params?: {
		page?: number;
		pageSize?: number;
		action?: string;
		from?: string;
		to?: string;
	}): Promise<PaginatedResponse<AuditLogEntryDto>> {
		const qs = buildQueryString(params ?? {});
		const url = qs ? `/api/v1/audit-log?${qs}` : '/api/v1/audit-log';
		const resp = await this.get<ApiResponse<PaginatedResponse<AuditLogEntryDto>>>(url);

		return this.unwrap(resp);
	}

	// Fleet SSH Sessions
	async getFleetSshSessions(params?: {
		page?: number;
		pageSize?: number;
		search?: string;
	}): Promise<PaginatedResponse<FleetSshSessionDto>> {
		const qs = buildQueryString(params ?? {});
		const url = qs ? `/api/v1/machines/ssh-sessions?${qs}` : '/api/v1/machines/ssh-sessions';
		const resp = await this.get<ApiResponse<PaginatedResponse<FleetSshSessionDto>>>(url);

		return this.unwrap(resp);
	}

	// Alert Rules
	async getAlertRules(): Promise<AlertRuleDto[]> {
		const resp = await this.get<ApiResponse<AlertRuleDto[]>>('/api/v1/alert-rules');

		return this.unwrap(resp);
	}

	async createAlertRule(req: CreateAlertRuleRequest): Promise<AlertRuleDto> {
		const resp = await this.post<ApiResponse<AlertRuleDto>>('/api/v1/alert-rules', req);

		return this.unwrap(resp);
	}

	async updateAlertRule(id: number, req: UpdateAlertRuleRequest): Promise<AlertRuleDto> {
		const resp = await this.put<ApiResponse<AlertRuleDto>>(`/api/v1/alert-rules/${id}`, req);

		return this.unwrap(resp);
	}

	async deleteAlertRule(id: number): Promise<void> {
		const resp = await this.del<ApiResponse<boolean>>(`/api/v1/alert-rules/${id}`);
		this.unwrap(resp);
	}

	// Alert Events
	async getAlertEvents(params?: {
		page?: number;
		pageSize?: number;
		status?: string;
		severity?: string;
	}): Promise<PaginatedResponse<AlertEventDto>> {
		const qs = buildQueryString(params ?? {});
		const url = qs ? `/api/v1/alert-events?${qs}` : '/api/v1/alert-events';
		const resp = await this.get<ApiResponse<PaginatedResponse<AlertEventDto>>>(url);

		return this.unwrap(resp);
	}

	async acknowledgeAlertEvent(id: number): Promise<void> {
		const resp = await this.post<ApiResponse<boolean>>(`/api/v1/alert-events/${id}/acknowledge`);
		this.unwrap(resp);
	}

	// Webhooks
	async getWebhooks(): Promise<WebhookEndpointDto[]> {
		const resp = await this.get<ApiResponse<WebhookEndpointDto[]>>('/api/v1/webhooks');

		return this.unwrap(resp);
	}

	async createWebhook(req: CreateWebhookRequest): Promise<WebhookEndpointDto> {
		const resp = await this.post<ApiResponse<WebhookEndpointDto>>('/api/v1/webhooks', req);

		return this.unwrap(resp);
	}

	async deleteWebhook(id: number): Promise<void> {
		const resp = await this.del<ApiResponse<boolean>>(`/api/v1/webhooks/${id}`);
		this.unwrap(resp);
	}

	// Signing Keys
	async getSigningKeys(): Promise<SigningKeyListResponse> {
		const resp = await this.get<ApiResponse<SigningKeyListResponse>>('/api/v1/signing-keys');

		return this.unwrap(resp);
	}

	async registerSigningKey(req: RegisterSigningKeyRequest): Promise<SigningKeyDto> {
		const resp = await this.post<ApiResponse<SigningKeyDto>>('/api/v1/signing-keys', req);

		return this.unwrap(resp);
	}

	async revokeSigningKey(id: number): Promise<void> {
		const resp = await this.del<ApiResponse<boolean>>(`/api/v1/signing-keys/${id}`);
		this.unwrap(resp);
	}

	// Remote Commands
	async sendCommand(req: SendCommandRequest): Promise<CommandDto> {
		const resp = await this.post<ApiResponse<CommandDto>>('/api/v1/commands', req);

		return this.unwrap(resp);
	}

	async getCommandHistory(
		machineId: number,
		params?: { page?: number; pageSize?: number }
	): Promise<CommandDto[]> {
		const qs = buildQueryString(params ?? {});
		const resp = await this.get<ApiResponse<CommandDto[]>>(
			`/api/v1/machines/${machineId}/commands${qs ? `?${qs}` : ''}`
		);

		return this.unwrap(resp);
	}

	async getCommandDetail(id: number): Promise<CommandDto> {
		const resp = await this.get<ApiResponse<CommandDto>>(`/api/v1/commands/${id}`);

		return this.unwrap(resp);
	}

	// Tenant Switching
	async switchTenant(tenantId: number): Promise<void> {
		const resp = await this.post<ApiResponse<object>>('/api/v1/tenants/switch', { tenantId });
		this.unwrap(resp);
	}

}
