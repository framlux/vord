// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;

namespace Framlux.FleetManagement.Test.Migrations;

/// <summary>
/// Schema and model round-trip tests for the <c>TenantDeletions</c> table created by
/// <see cref="Database.Migrations.InitialMigration"/>.
/// </summary>
public class TenantDeletionsSchemaTests
{
    [Test]
    public async Task TenantDeletions_InsertAndRead_RoundTripsAllFields()
    {
        using TestDatabaseFactory dbFactory = new();

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Deleted Tenant", createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset requestedAt = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset scheduledPurgeAt = requestedAt.AddDays(30);

        TenantDeletion deletion = new()
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = userId,
            RequestedAt = requestedAt,
            ScheduledPurgeAt = scheduledPurgeAt,
            Status = TenantDeletionStatus.Deactivated,
            PurgedAt = null,
            Reason = "Customer requested account closure.",
        };

        int deletionId = await dbFactory.Context.InsertWithInt32IdentityAsync(deletion);

        TenantDeletion? result = await dbFactory.Context.TenantDeletions
            .FirstOrDefaultAsync(d => d.Id == deletionId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(deletionId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.TenantExternalId).IsEqualTo(tenant.ExternalId);
        await Assert.That(result.TenantName).IsEqualTo(tenant.Name);
        await Assert.That(result.RequestedByUserId).IsEqualTo(userId);
        await Assert.That(result.RequestedAt).IsEqualTo(requestedAt);
        await Assert.That(result.ScheduledPurgeAt).IsEqualTo(scheduledPurgeAt);
        await Assert.That(result.Status).IsEqualTo(TenantDeletionStatus.Deactivated);
        await Assert.That(result.PurgedAt).IsNull();
        await Assert.That(result.Reason).IsEqualTo("Customer requested account closure.");
    }

    [Test]
    public async Task TenantDeletions_SecondActiveDeletionForSameTenant_ThrowsUniqueConstraintViolation()
    {
        using TestDatabaseFactory dbFactory = new();

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Double Deleted Tenant", createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;

        TenantDeletion first = new()
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = userId,
            RequestedAt = requestedAt,
            ScheduledPurgeAt = requestedAt.AddDays(30),
            Status = TenantDeletionStatus.Deactivated,
        };
        await dbFactory.Context.InsertWithInt32IdentityAsync(first);

        TenantDeletion second = new()
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = userId,
            RequestedAt = requestedAt,
            ScheduledPurgeAt = requestedAt.AddDays(30),
            Status = TenantDeletionStatus.Deactivated,
        };

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await dbFactory.Context.InsertWithInt32IdentityAsync(second));
    }

    [Test]
    public async Task TenantDeletions_SecondDeletionAfterFirstRestored_IsAllowed()
    {
        using TestDatabaseFactory dbFactory = new();

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Restored Tenant", createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;

        TenantDeletion first = new()
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = userId,
            RequestedAt = requestedAt,
            ScheduledPurgeAt = requestedAt.AddDays(30),
            Status = TenantDeletionStatus.Deactivated,
        };
        int firstId = await dbFactory.Context.InsertWithInt32IdentityAsync(first);

        await dbFactory.Context.TenantDeletions
            .Where(d => d.Id == firstId)
            .Set(d => d.Status, TenantDeletionStatus.Restored)
            .UpdateAsync();

        TenantDeletion second = new()
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = userId,
            RequestedAt = requestedAt.AddDays(1),
            ScheduledPurgeAt = requestedAt.AddDays(31),
            Status = TenantDeletionStatus.Deactivated,
        };

        int secondId = await dbFactory.Context.InsertWithInt32IdentityAsync(second);

        await Assert.That(secondId).IsNotEqualTo(0);
    }
}
