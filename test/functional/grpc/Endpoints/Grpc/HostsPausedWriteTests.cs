// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests, over the real gRPC pipeline, for the writes that move a tenant's machine limit
/// from the control plane: each records how many hosts the new limit leaves paused, in the write's own
/// transaction, and clears the tenant's cached cutoff once it has committed.
/// </summary>
public sealed class HostsPausedWriteTests
{
    private const string PermittedClientSubject = "billing-api.vord-fleet.svc.cluster.local";

    private static readonly DateTimeOffset TermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    [Test]
    public async Task SetTenantOverride_WhenTheLimitLeavesHostsPaused_RecordsThemAndClearsTheCachedCutoff()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Pro, machines: 6);
        IDatabase redis = await SeedCachedEntriesAsync(factory, tenantId);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        SetTenantOverrideResponse response = await fleetAdmin.SetTenantOverrideAsync(
            new SetTenantOverrideRequest
            {
                TenantExternalId = extId,
                MachineLimit = 4,
                RetentionDays = -1,
                AlertRuleLimit = -1,
                WebhookLimit = -1,
            },
            Headers());

        await Assert.That(response.Success).IsTrue();
        AuditLogEntry paused = await SingleHostsPausedAsync(db, tenantId);
        await Assert.That(paused.Details).Contains("\"paused\":2");
        await Assert.That(paused.Details).Contains("\"limit\":4");
        await Assert.That(paused.Details).Contains("\"registered\":6");
        await AssertCachedEntriesClearedAsync(redis, tenantId);
    }

    [Test]
    public async Task SetTenantOverride_WhenTheLimitCoversEveryHost_RecordsNothing()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Pro, machines: 6);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        await fleetAdmin.SetTenantOverrideAsync(
            new SetTenantOverrideRequest
            {
                TenantExternalId = extId,
                MachineLimit = 6,
                RetentionDays = -1,
                AlertRuleLimit = -1,
                WebhookLimit = -1,
            },
            Headers());

        await Assert.That(await db.AuditLog.CountAsync(a => (a.TenantId == tenantId) && (a.Action == AuditAction.HostsPaused))).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveTenantOverride_WhenTheTierLimitLeavesHostsPaused_RecordsThemAndClearsTheCachedCutoff()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Free, machines: 6);
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        IDatabase redis = await SeedCachedEntriesAsync(factory, tenantId);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        RemoveTenantOverrideResponse response = await fleetAdmin.RemoveTenantOverrideAsync(
            new RemoveTenantOverrideRequest { TenantExternalId = extId }, Headers());

        await Assert.That(response.Success).IsTrue();
        AuditLogEntry paused = await SingleHostsPausedAsync(db, tenantId);
        await Assert.That(paused.Details).Contains("\"paused\":3");
        await Assert.That(paused.Details).Contains("\"limit\":3");
        await AssertCachedEntriesClearedAsync(redis, tenantId);
    }

    [Test]
    public async Task UpdateTenantSubscription_ToFreeWithMoreHostsThanItAllows_RecordsThemAndClearsTheCachedCutoff()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Pro, machines: 5);
        IDatabase redis = await SeedCachedEntriesAsync(factory, tenantId);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        UpdateTenantSubscriptionResponse response = await fleetAdmin.UpdateTenantSubscriptionAsync(
            new UpdateTenantSubscriptionRequest { TenantExternalId = extId, Tier = BillingTier.Free, Status = "Active" },
            Headers());

        await Assert.That(response.Success).IsTrue();
        AuditLogEntry paused = await SingleHostsPausedAsync(db, tenantId);
        await Assert.That(paused.Details).Contains("\"paused\":2");
        await Assert.That(paused.Details).Contains("\"limit\":3");
        await AssertCachedEntriesClearedAsync(redis, tenantId);
    }

    [Test]
    public async Task DowngradeToFreeBillingAction_WithMoreHostsThanFreeAllows_RecordsThemAndClearsTheCachedCutoff()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Pro, machines: 5);
        IDatabase redis = await SeedCachedEntriesAsync(factory, tenantId);
        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient gateway = new(channel);

        BillingActionResponse response = await gateway.ProcessBillingActionAsync(
            new BillingActionRequest { TenantExternalId = extId, Action = BillingAction.DowngradeToFree }, Headers());

        await Assert.That(response.Success).IsTrue();
        AuditLogEntry paused = await SingleHostsPausedAsync(db, tenantId);
        await Assert.That(paused.Details).Contains("\"paused\":2");
        await AssertCachedEntriesClearedAsync(redis, tenantId);
    }

    [Test]
    public async Task ApplyEnterpriseAgreement_WithAMachineLimitBelowTheHostCount_RecordsThePausedHostsBeforeTheAgreementRow()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Team, machines: 6);
        IDatabase redis = await SeedCachedEntriesAsync(factory, tenantId);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        ApplyEnterpriseAgreementResponse response = await fleetAdmin.ApplyEnterpriseAgreementAsync(
            new ApplyEnterpriseAgreementRequest
            {
                TenantExternalId = extId,
                AgreementId = 42,
                Revision = 1,
                MachineLimit = 4,
                RetentionDays = 180,
                MemberLimit = int.MaxValue,
                AlertRuleLimit = 40,
                WebhookLimit = 20,
                TermEnd = Timestamp.FromDateTimeOffset(TermEnd),
            },
            Headers());

        await Assert.That(response.Outcome).IsEqualTo("applied");
        AuditLogEntry paused = await SingleHostsPausedAsync(db, tenantId);
        AuditLogEntry agreement = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.EnterpriseAgreementApplied))
            .SingleAsync();
        await Assert.That(paused.Details).Contains("\"paused\":2");
        await Assert.That(paused.Details).Contains("\"limit\":4");
        await Assert.That(paused.Id).IsLessThan(agreement.Id);
        await AssertCachedEntriesClearedAsync(redis, tenantId);
    }

    private static async Task<AuditLogEntry> SingleHostsPausedAsync(DatabaseContext db, int tenantId)
    {
        AuditLogEntry entry = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.HostsPaused))
            .SingleAsync();

        return entry;
    }

    /// <summary>
    /// Plants the two entries a stale replica could be holding for the tenant, in the fake Redis the
    /// server under test shares.
    /// </summary>
    private static async Task<IDatabase> SeedCachedEntriesAsync(FunctionalTestFactory factory, int tenantId)
    {
        IDatabase redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.StringSetAsync(QuotaPauseService.CacheKeyFor(tenantId), "after:1");
        await redis.StringSetAsync(CachingSubscriptionRepository.CacheKeyFor(tenantId), "{}");

        return redis;
    }

    private static async Task AssertCachedEntriesClearedAsync(IDatabase redis, int tenantId)
    {
        await Assert.That((await redis.StringGetAsync(QuotaPauseService.CacheKeyFor(tenantId))).HasValue).IsFalse();
        await Assert.That((await redis.StringGetAsync(CachingSubscriptionRepository.CacheKeyFor(tenantId))).HasValue).IsFalse();
    }

    private static Metadata Headers()
    {
        return new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db, string externalId, SubscriptionTier tier, int machines)
    {
        Tenant tenant = new()
        {
            Name = $"tenant-{Guid.NewGuid():N}",
            ExternalId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        for (int i = 0; i < machines; i++)
        {
            await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
        }

        return tenantId;
    }
}
