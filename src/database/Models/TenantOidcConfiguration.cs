// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Stores custom OIDC configuration for Team-tier tenants.
/// </summary>
[Table(Name = TableNames.TenantOidcConfigurations)]
public sealed class TenantOidcConfiguration
{
    /// <summary>
    /// The unique identifier for the configuration.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The ID of the tenant this configuration belongs to.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The OIDC authority URL.
    /// </summary>
    [Column("Authority"), NotNull]
    public required string Authority { get; set; }

    /// <summary>
    /// The OIDC client ID.
    /// </summary>
    [Column("ClientId"), NotNull]
    public required string ClientId { get; set; }

    /// <summary>
    /// The OIDC client secret.
    /// </summary>
    [Column("ClientSecret"), NotNull]
    public required string ClientSecret { get; set; }

    /// <summary>
    /// Optional discovery endpoint override.
    /// </summary>
    [Column("MetadataAddress"), Nullable]
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// The email domain to restrict logins to (e.g. "example.com").
    /// </summary>
    [Column("EmailDomain"), NotNull]
    public required string EmailDomain { get; set; }

    /// <summary>
    /// Whether this OIDC configuration is enabled.
    /// </summary>
    [Column("IsEnabled"), NotNull]
    public required bool IsEnabled { get; set; }

    /// <summary>
    /// When the configuration was created.
    /// </summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the configuration was last updated.
    /// </summary>
    [Column("UpdatedAt"), NotNull]
    public required DateTimeOffset UpdatedAt { get; set; }
}
