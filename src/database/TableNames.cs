// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database;

internal static class TableNames
{
    public const string ServerConfigurationSettings = "ConfigurationSettings";
    public const string Machines = "Machines";
    public const string Users = "UserAccounts";
    public const string Tenants = "Tenants";
    public const string UserTenantRoles = "UserTenantRoles";
    public const string MachineTelemetry = "MachineTelemetry";
    public const string MachineCertificates = "MachineCertificates";
    public const string MachineStateSummary = "MachineStateSummary";
    public const string MachineStateDetail = "MachineStateDetail";
    public const string TenantSubscriptions = "TenantSubscriptions";
    public const string TenantOidcConfigurations = "TenantOidcConfigurations";
    public const string TenantInvitations = "TenantInvitations";
    public const string RegistrationTokens = "RegistrationTokens";
    public const string DataExportJobs = "DataExportJobs";
    public const string AuditLog = "AuditLog";
    public const string AlertRules = "AlertRules";
    public const string AlertEvents = "AlertEvents";
    public const string WebhookEndpoints = "WebhookEndpoints";
    public const string UserSigningKeys = "UserSigningKeys";
    public const string RemoteCommands = "RemoteCommands";
}
