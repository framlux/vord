// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Billing;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using LinqToDB.Async;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for how the billing endpoints treat a tenant on an Enterprise agreement: the
/// plan is invoiced outside Stripe, so the billing pages must answer from the agreement without
/// asking billing-api, and every self-serve plan change must be refused.
/// </summary>
public sealed class EnterpriseBillingEndpointTests
{
    private static readonly DateTimeOffset AgreementPeriodEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    [Test]
    public async Task GetSubscription_Enterprise_ReportsAgreementLimitsWithoutAskingBillingApi()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Enterprise);
        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Enterprise");
        await Assert.That(data.GetProperty("machineLimit").GetInt32()).IsEqualTo(500);
        await Assert.That(data.GetProperty("retentionDays").GetInt32()).IsEqualTo(180);
        await Assert.That(data.GetProperty("alertRuleLimit").GetInt32()).IsEqualTo(40);
        await Assert.That(data.GetProperty("webhookLimit").GetInt32()).IsEqualTo(20);
        await Assert.That(data.GetProperty("memberLimit").GetInt32()).IsEqualTo(75);
        await Assert.That(data.GetProperty("currentPeriodEnd").GetDateTimeOffset()).IsEqualTo(AgreementPeriodEnd);
        await Assert.That(data.GetProperty("cancelAtPeriodEnd").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("billingInterval").ValueKind).IsEqualTo(JsonValueKind.Null);
        await factory.BillingApiClientMock.DidNotReceive()
            .GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetSubscription_ReportsMemberCountAsActiveMembersPlusPendingInvitations()
    {
        // The count shown must be the one the member limit is enforced against: an active member
        // and a pending invitation each hold a seat, while a revoked invitation does not.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Enterprise);
        await SeedTenantAdminAsync(db, tenantId);
        await db.InsertAsync(TestDataBuilder.BuildInvitation(tenantId: tenantId));
        await db.InsertAsync(TestDataBuilder.BuildInvitation(tenantId: tenantId));
        await db.InsertAsync(TestDataBuilder.BuildInvitation(tenantId: tenantId, status: InvitationStatus.Revoked));
        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("memberCount").GetInt32()).IsEqualTo(3);
        await Assert.That(data.GetProperty("memberLimit").GetInt32()).IsEqualTo(75);
    }

    [Test]
    public async Task GetSubscription_Team_StillAsksBillingApi()
    {
        // Regression: skipping the Stripe lookup is for Enterprise only; a Team tenant keeps its
        // cancellation state and billing interval from billing-api.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Team, withAgreementOverride: false);
        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        await Assert.That(data.GetProperty("tier").GetString()).IsEqualTo("Team");
        await factory.BillingApiClientMock.Received(1)
            .GetSubscriptionStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("/api/v1/billing/invoices")]
    [Arguments("/api/v1/billing/upcoming-invoice")]
    public async Task InvoiceEndpoints_Enterprise_AnswerEmptyWithoutAskingBillingApi(string path)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Enterprise);
        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        JsonElement data = await ExtractDataElement(response);
        if (path.EndsWith("/invoices", StringComparison.Ordinal))
        {
            await Assert.That(data.ValueKind).IsEqualTo(JsonValueKind.Array);
            await Assert.That(data.GetArrayLength()).IsEqualTo(0);
        }
        else
        {
            await Assert.That(data.GetProperty("hasInvoice").GetBoolean()).IsFalse();
        }

        await factory.BillingApiClientMock.DidNotReceive()
            .ListInvoicesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await factory.BillingApiClientMock.DidNotReceive()
            .GetUpcomingInvoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("/api/v1/billing/invoices")]
    [Arguments("/api/v1/billing/upcoming-invoice")]
    public async Task InvoiceEndpoints_Team_StillAskBillingApi(string path)
    {
        // Regression: only Enterprise short-circuits; a Stripe-billed tenant's invoices still come
        // from billing-api.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Team, withAgreementOverride: false);
        HttpClient client = BuildViewerClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(path);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        if (path.EndsWith("/invoices", StringComparison.Ordinal))
        {
            await factory.BillingApiClientMock.Received(1)
                .ListInvoicesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await factory.BillingApiClientMock.Received(1)
                .GetUpcomingInvoiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    [Arguments("/api/v1/billing/downgrade", """{"targetTier":"pro"}""")]
    [Arguments("/api/v1/billing/downgrade", """{"targetTier":"free"}""")]
    [Arguments("/api/v1/billing/cancel", "{}")]
    [Arguments("/api/v1/billing/reactivate", "{}")]
    [Arguments("/api/v1/billing/resume", "{}")]
    public async Task CustomerBillingActions_Enterprise_AreRefusedWithConflict(string path, string body)
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantAsync(db, SubscriptionTier.Enterprise);
        int userId = await SeedTenantAdminAsync(db, tenantId);
        HttpClient client = BuildAdminClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync(
            path, new StringContent(body, Encoding.UTF8, "application/json"));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString())
            .IsEqualTo(BillingEndpointGuards.EnterpriseAgreementMessage);

        TenantSubscription stored = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(stored.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(stored.Status).IsEqualTo(SubscriptionStatus.Active);

        IBillingApiClient billingApi = factory.BillingApiClientMock;
        await billingApi.DidNotReceive().CancelSubscriptionAsync(
            Arg.Any<string>(), Arg.Any<PendingActionType>(), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().SwapSubscriptionPriceAsync(
            Arg.Any<string>(), Arg.Any<BillingTier>(), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().ResumeSubscriptionAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await billingApi.DidNotReceive().GetSubscriptionStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== Helpers ==========

    private static HttpClient BuildViewerClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithExternalId($"ext-ent-{Guid.NewGuid():N}")
            .WithEmail("enterprise@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static HttpClient BuildAdminClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    /// <summary>
    /// Extracts the "data" element from the API response JSON, asserting the outer success flag.
    /// </summary>
    private static async Task<JsonElement> ExtractDataElement(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        return root.GetProperty("data");
    }

    /// <summary>
    /// Seeds an active tenant on the given tier. An Enterprise tenant carries the agreement's
    /// limits as an override, the way applying an agreement leaves it.
    /// </summary>
    private static async Task<int> SeedTenantAsync(
        DatabaseContext db,
        SubscriptionTier tier,
        bool withAgreementOverride = true)
    {
        Tenant tenant = new()
        {
            Name = $"Enterprise Tenant {Guid.NewGuid():N}",
            ExternalId = $"ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = AgreementPeriodEnd,
            AppliedAgreementRevision = (tier == SubscriptionTier.Enterprise) ? 1 : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        if (withAgreementOverride)
        {
            TenantSubscriptionOverride agreement = new()
            {
                TenantId = tenantId,
                MachineLimit = 500,
                RetentionDays = 180,
                AlertRuleLimit = 40,
                WebhookLimit = 20,
                MemberLimit = 75,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await db.InsertAsync(agreement);
        }

        return tenantId;
    }

    private static async Task<int> SeedTenantAdminAsync(DatabaseContext db, int tenantId)
    {
        UserAccount user = new()
        {
            ExternalId = $"ext-ent-admin-{Guid.NewGuid():N}",
            Username = $"enterprise-admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return user.Id;
    }
}
