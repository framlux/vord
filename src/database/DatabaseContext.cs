// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Database;

/// <summary>
/// Represents the database context for the application.
/// </summary>
/// <param name="options"></param>
public sealed class DatabaseContext(DataOptions<DatabaseContext> options) : DataConnection(options.Options)
{
    /// <summary>
    /// Represents the collection of machines in the database.
    /// </summary>
    public ITable<Machine> Machines => this.GetTable<Machine>();

    /// <summary>
    /// Represents the collection of user accounts in the database.
    /// </summary>
    public ITable<UserAccount> UserAccounts => this.GetTable<UserAccount>();

    /// <summary>
    /// Represents the collection of server configuration settings in the database.
    /// </summary>
    public ITable<ServerConfigurationSettings> ServerConfigurationSettings => this.GetTable<ServerConfigurationSettings>();

    /// <summary>
    /// Represents the collection of tenants in the database.
    /// </summary>
    public ITable<Tenant> Tenants => this.GetTable<Tenant>();

    /// <summary>
    /// Represents the collection of user tenant roles in the database.
    /// </summary>
    public ITable<UserTenantRole> UserTenantRoles => this.GetTable<UserTenantRole>();

    /// <summary>
    /// Represents the collection of machine telemetry records in the database.
    /// </summary>
    public ITable<MachineTelemetry> MachineTelemetry => this.GetTable<MachineTelemetry>();

    /// <summary>
    /// Represents the collection of machine state summary records for fleet-level queries.
    /// </summary>
    public ITable<MachineStateSummary> MachineStateSummaries => this.GetTable<MachineStateSummary>();

    /// <summary>
    /// Represents the collection of machine state detail records for single-machine views.
    /// </summary>
    public ITable<MachineStateDetail> MachineStateDetails => this.GetTable<MachineStateDetail>();

    /// <summary>
    /// Represents the collection of tenant subscriptions in the database.
    /// </summary>
    public ITable<TenantSubscription> TenantSubscriptions => this.GetTable<TenantSubscription>();

    /// <summary>
    /// Represents the collection of tenant OIDC configurations in the database.
    /// </summary>
    public ITable<TenantOidcConfiguration> TenantOidcConfigurations => this.GetTable<TenantOidcConfiguration>();

    /// <summary>
    /// Represents the collection of tenant invitations in the database.
    /// </summary>
    public ITable<TenantInvitation> TenantInvitations => this.GetTable<TenantInvitation>();

    /// <summary>
    /// Represents the collection of registration tokens in the database.
    /// </summary>
    public ITable<RegistrationToken> RegistrationTokens => this.GetTable<RegistrationToken>();

    /// <summary>
    /// Represents the collection of data export jobs in the database.
    /// </summary>
    public ITable<DataExportJob> DataExportJobs => this.GetTable<DataExportJob>();

    /// <summary>
    /// Represents the collection of audit log entries in the database.
    /// </summary>
    public ITable<AuditLogEntry> AuditLog => this.GetTable<AuditLogEntry>();

    /// <summary>
    /// Represents the collection of alert rules in the database.
    /// </summary>
    public ITable<AlertRule> AlertRules => this.GetTable<AlertRule>();

    /// <summary>
    /// Represents the collection of alert events in the database.
    /// </summary>
    public ITable<AlertEvent> AlertEvents => this.GetTable<AlertEvent>();

    /// <summary>
    /// Represents the collection of alert rule machine associations for scoping rules to specific machines.
    /// </summary>
    public ITable<AlertRuleMachine> AlertRuleMachines => this.GetTable<AlertRuleMachine>();

    /// <summary>
    /// Represents the per-rule-per-machine condition tracking rows used by AlertEvaluationJob to
    /// enforce the DurationMinutes window. Replaces the previous Redis-backed condition keys.
    /// </summary>
    public ITable<AlertConditionState> AlertConditionStates => this.GetTable<AlertConditionState>();

    /// <summary>
    /// Represents the collection of integration endpoints for alert delivery.
    /// </summary>
    public ITable<IntegrationEndpoint> IntegrationEndpoints => this.GetTable<IntegrationEndpoint>();

    /// <summary>
    /// Per-(event, integration) delivery-success records used by IntegrationDeliveryJob to enforce
    /// idempotency on Hangfire retries. A row exists if and only if the delivery succeeded once.
    /// </summary>
    public ITable<IntegrationDeliveryAttempt> IntegrationDeliveryAttempts => this.GetTable<IntegrationDeliveryAttempt>();

    /// <summary>
    /// Represents the collection of user signing keys for remote command authorization.
    /// </summary>
    public ITable<UserSigningKey> UserSigningKeys => this.GetTable<UserSigningKey>();

    /// <summary>
    /// Represents the collection of remote commands sent to machines.
    /// </summary>
    public ITable<RemoteCommand> RemoteCommands => this.GetTable<RemoteCommand>();

    /// <summary>
    /// Represents the collection of machine authorized key records in the database.
    /// </summary>
    public ITable<MachineAuthorizedKey> MachineAuthorizedKeys => this.GetTable<MachineAuthorizedKey>();

    /// <summary>
    /// Represents the collection of tier feature limit configurations.
    /// </summary>
    public ITable<TierFeatureLimit> TierFeatureLimits => this.GetTable<TierFeatureLimit>();

    /// <summary>
    /// Represents the collection of per-tenant subscription limit overrides.
    /// </summary>
    public ITable<TenantSubscriptionOverride> TenantSubscriptionOverrides => this.GetTable<TenantSubscriptionOverride>();
}
