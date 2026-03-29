// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

﻿using Framlux.FleetManagement.Database.Models;
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
    /// Represents the collection of machine certificate records in the database.
    /// </summary>
    public ITable<MachineCertificate> MachineCertificates => this.GetTable<MachineCertificate>();

    /// <summary>
    /// Represents the collection of machine state cache records in the database.
    /// </summary>
    public ITable<MachineState> MachineStates => this.GetTable<MachineState>();

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
    /// Represents the collection of webhook endpoints in the database.
    /// </summary>
    public ITable<WebhookEndpoint> WebhookEndpoints => this.GetTable<WebhookEndpoint>();

    /// <summary>
    /// Represents the collection of user signing keys for remote command authorization.
    /// </summary>
    public ITable<UserSigningKey> UserSigningKeys => this.GetTable<UserSigningKey>();

    /// <summary>
    /// Represents the collection of remote commands sent to machines.
    /// </summary>
    public ITable<RemoteCommand> RemoteCommands => this.GetTable<RemoteCommand>();
}
