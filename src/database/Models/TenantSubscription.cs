// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents a tenant's subscription plan and billing information.
/// </summary>
[Table(Name = TableNames.TenantSubscriptions)]
public sealed class TenantSubscription
{
    /// <summary>
    /// The unique identifier for the subscription.
    /// </summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>
    /// The ID of the tenant this subscription belongs to.
    /// </summary>
    [Column("TenantId"), NotNull]
    public required int TenantId { get; set; }

    /// <summary>
    /// The associated tenant.
    /// </summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>
    /// The subscription tier.
    /// </summary>
    [Column("Tier"), NotNull]
    public required SubscriptionTier Tier { get; set; }

    /// <summary>
    /// The current status of the subscription.
    /// </summary>
    [Column("Status"), NotNull]
    public required SubscriptionStatus Status { get; set; }

    /// <summary>
    /// The end of the current billing period from Stripe.
    /// </summary>
    [Column("CurrentPeriodEnd"), Nullable]
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>
    /// Whether the subscription is scheduled to cancel at the end of the current billing period.
    /// Mirrored from Stripe by the StripeSyncJob so the UI can reflect a pending cancellation
    /// before the subscription actually transitions to a canceled status at period end.
    /// </summary>
    [Column("CancelAtPeriodEnd"), NotNull]
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>
    /// When the subscription was created.
    /// </summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the subscription was last updated.
    /// </summary>
    [Column("UpdatedAt"), NotNull]
    public required DateTimeOffset UpdatedAt { get; set; }
}
