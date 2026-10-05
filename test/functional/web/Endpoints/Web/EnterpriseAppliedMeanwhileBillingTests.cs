// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the customer billing endpoints that write the subscription row themselves,
/// when an enterprise agreement is applied between the gated read and the write. The read can be
/// served from the subscription cache for up to its TTL, so a tenant that is already Enterprise can
/// still look like a Team, Free or canceled one; the guarded write is what protects it.
/// </summary>
public sealed class EnterpriseAppliedMeanwhileBillingTests
{
    private static readonly DateTimeOffset AgreementTermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    [Test]
    public async Task TeamToProDowngrade_WhenAnAgreementWasAppliedAfterTheRead_IsRefusedAndTouchesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAsync(db, SubscriptionTier.Team, SubscriptionStatus.Active);
        await db.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: tenantId, isEnabled: true));
        AlertRule customRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, isCustom: true, isEnabled: true);
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);
        await CacheSubscriptionAsync(factory, tenantId);
        await ApplyAgreementBehindTheCacheAsync(db, tenantId);
        HttpClient client = BuildAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade", Json("""{"targetTier":"pro"}"""));

        await AssertRefusedAsConflictAsync(response);
        TenantSubscription stored = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(stored.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        TenantOidcConfiguration oidc = await db.TenantOidcConfigurations.FirstAsync(c => c.TenantId == tenantId);
        await Assert.That(oidc.IsEnabled).IsTrue();
        AlertRule rule = await db.AlertRules.FirstAsync(r => r.Id == customRule.Id);
        await Assert.That(rule.IsEnabled).IsTrue();
        await Assert.That(await CountAuditRowsAsync(db, tenantId, AuditAction.SubscriptionDowngradeRequested)).IsEqualTo(0);
        await factory.BillingApiClientMock.DidNotReceive().SwapSubscriptionPriceAsync(
            Arg.Any<string>(), Arg.Any<BillingTier>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FreeCancellation_WhenAnAgreementWasAppliedAfterTheRead_IsRefusedAndWritesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAsync(db, SubscriptionTier.Free, SubscriptionStatus.Active);
        await CacheSubscriptionAsync(factory, tenantId);
        await ApplyAgreementBehindTheCacheAsync(db, tenantId);
        HttpClient client = BuildAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", Json("{}"));

        await AssertRefusedAsConflictAsync(response);
        TenantSubscription stored = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(stored.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(stored.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(await CountAuditRowsAsync(db, tenantId, AuditAction.SubscriptionCancelRequested)).IsEqualTo(0);
    }

    [Test]
    public async Task Reactivation_WhenAnAgreementWasAppliedAfterTheRead_IsRefusedAndWritesNothing()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAsync(db, SubscriptionTier.Free, SubscriptionStatus.Canceled);
        await CacheSubscriptionAsync(factory, tenantId);
        await ApplyAgreementBehindTheCacheAsync(db, tenantId);
        HttpClient client = BuildAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", Json("{}"));

        await AssertRefusedAsConflictAsync(response);
        TenantSubscription stored = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(stored.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(stored.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(await CountAuditRowsAsync(db, tenantId, AuditAction.SubscriptionUpgraded)).IsEqualTo(0);
    }

    [Test]
    public async Task TeamToProDowngrade_WhenNoAgreementWasApplied_StillFreezesTeamResourcesInTheSameCall()
    {
        // Regression for the refusal above: the guarded write must still let an ordinary downgrade
        // through, with its cleanup applied.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantAsync(db, SubscriptionTier.Team, SubscriptionStatus.Active);
        await db.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: tenantId, isEnabled: true));
        AlertRule customRule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, isCustom: true, isEnabled: true);
        customRule.Id = await db.InsertWithInt32IdentityAsync(customRule);
        HttpClient client = BuildAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/billing/downgrade", Json("""{"targetTier":"pro"}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        TenantSubscription stored = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(stored.Tier).IsEqualTo(SubscriptionTier.Pro);
        TenantOidcConfiguration oidc = await db.TenantOidcConfigurations.FirstAsync(c => c.TenantId == tenantId);
        await Assert.That(oidc.IsEnabled).IsFalse();
        AlertRule rule = await db.AlertRules.FirstAsync(r => r.Id == customRule.Id);
        await Assert.That(rule.IsEnabled).IsFalse();
        await Assert.That(await CountAuditRowsAsync(db, tenantId, AuditAction.SubscriptionDowngradeRequested)).IsEqualTo(1);
        await factory.BillingApiClientMock.Received(1).SwapSubscriptionPriceAsync(
            Arg.Any<string>(), BillingTier.Pro, Arg.Any<CancellationToken>());
    }

    // ========== Helpers ==========

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static async Task AssertRefusedAsConflictAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString())
            .IsEqualTo(BillingEndpointGuards.EnterpriseAgreementMessage);
    }

    /// <summary>
    /// Puts the tenant's current subscription into the shared cache, the way an earlier request to
    /// any page would have, so a later change behind the cache's back is invisible to the gated read.
    /// </summary>
    private static async Task CacheSubscriptionAsync(FunctionalTestFactory factory, int tenantId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        ISubscriptionRepository subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        TenantSubscription? cached = await subscriptions.GetSubscriptionForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(cached).IsNotNull();
    }

    /// <summary>
    /// Applies an agreement the way the apply path leaves the row, but straight into the database so
    /// the cached copy keeps describing the tenant as it was.
    /// </summary>
    private static async Task ApplyAgreementBehindTheCacheAsync(DatabaseContext db, int tenantId)
    {
        int updated = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, SubscriptionTier.Enterprise)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.CurrentPeriodEnd, (DateTimeOffset?)AgreementTermEnd)
            .Set(s => s.AppliedAgreementRevision, (int?)1)
            .UpdateAsync();
        await Assert.That(updated).IsEqualTo(1);
    }

    private static async Task<int> CountAuditRowsAsync(DatabaseContext db, int tenantId, AuditAction action)
    {
        int count = await db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == action))
            .CountAsync();

        return count;
    }

    private static HttpClient BuildAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static async Task<(int TenantId, int UserId)> SeedTenantAsync(
        DatabaseContext db, SubscriptionTier tier, SubscriptionStatus status)
    {
        Tenant tenant = new()
        {
            Name = $"Applied Meanwhile Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        UserAccount user = new()
        {
            ExternalId = $"ext-meanwhile-admin-{Guid.NewGuid():N}",
            Username = $"meanwhile-admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        return (tenant.Id, user.Id);
    }
}
