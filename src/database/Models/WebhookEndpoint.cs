// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a webhook endpoint for delivering alert notifications.
/// </summary>
[Table(Name = TableNames.WebhookEndpoints)]
public sealed class WebhookEndpoint
{
    /// <summary>The unique identifier.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The tenant this webhook belongs to.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The associated tenant.</summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>A name for this webhook endpoint.</summary>
    [Column("Name"), NotNull]
    public required string Name { get; set; }

    /// <summary>The URL to POST alerts to.</summary>
    [Column("Url"), NotNull]
    public required string Url { get; set; }

    /// <summary>The HMAC-SHA256 signing secret.</summary>
    [Column("Secret"), NotNull]
    public required string Secret { get; set; }

    /// <summary>Whether this webhook is enabled.</summary>
    [Column("IsEnabled"), NotNull]
    public bool IsEnabled { get; set; }

    /// <summary>The user who created this webhook.</summary>
    [Column("CreatedByUserId"), NotNull]
    public int CreatedByUserId { get; set; }

    /// <summary>When the webhook was created.</summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }
}
