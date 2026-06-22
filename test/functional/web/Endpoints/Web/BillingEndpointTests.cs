// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using LinqToDB.Async;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for billing cancel subscription endpoint.
/// </summary>
public sealed class BillingEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedBillingEnvironment(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Billing Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-billing-user-{Guid.NewGuid():N}",
            Username = $"billinguser-{Guid.NewGuid():N}@example.com",
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
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    [Test]
    public async Task CancelSubscription_NoTenant_Returns403()
    {
        // A user without an active tenant should be rejected by the TenantAdmin policy
        // before the handler executes, resulting in a 403 Forbidden
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task CancelSubscription_AlreadyCancelling_ReturnsOkWithIdempotentMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Configure the billing mock to report that Stripe already has cancel_at_period_end set,
        // simulating the state after a previous cancellation request was processed.
        factory.BillingApiClientMock.GetSubscriptionStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new StripeSubscriptionStatus(true, "active", string.Empty, 0, null, BillingTier.Unspecified)));

        // Cancel when Stripe already reflects a pending cancellation
        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("already set to cancel");
    }

    [Test]
    public async Task CancelSubscription_ActiveSubscription_ReturnsSuccessWithCancellationConfirmation()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("canceled at the end of the current billing period");
    }

    [Test]
    public async Task CancelSubscription_ActiveSubscription_DelegatesCancellationToBillingApi()
    {
        // Cancellation for paid tiers is delegated to the billing-api via gRPC.
        // The fleet server does not modify the subscription record directly;
        // instead it calls the billing-api and records an audit log entry.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify the subscription status was not changed (billing-api handles that via webhook)
        using DatabaseContext verifyDb = factory.CreateDbContext();
        TenantSubscription? subscription = await verifyDb.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Status).IsEqualTo(SubscriptionStatus.Active);

        // Verify an audit log entry was created for the cancellation request
        AuditLogEntry? auditEntry = await verifyDb.AuditLog
            .Where(a => a.TenantId == tenantId && a.Action == AuditAction.SubscriptionCancelRequested)
            .FirstOrDefaultAsync();
        await Assert.That(auditEntry).IsNotNull();
    }

    [Test]
    public async Task CancelSubscription_AlreadyCancelled_ReturnsOkIdempotent()
    {
        // A subscription that has already been fully canceled should return an idempotent
        // success response with the "already canceled" message from the Canceled status guard
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.Canceled);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("already canceled");
    }

    [Test]
    public async Task CancelSubscription_PastDueSubscription_CancelsSuccessfully()
    {
        // A past-due subscription that has not been marked for cancellation
        // should still allow cancellation since the endpoint does not gate on status
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.PastDue);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("canceled at the end of the current billing period");
    }

    [Test]
    public async Task CancelSubscription_PastDueSubscription_DelegatesCancellationToBillingApi()
    {
        // A past-due subscription that is not yet canceled delegates cancellation
        // to the billing-api and preserves the PastDue status locally.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(
            db,
            status: SubscriptionStatus.PastDue);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify the subscription status was preserved (billing-api handles the Stripe side)
        using DatabaseContext verifyDb = factory.CreateDbContext();
        TenantSubscription? subscription = await verifyDb.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Status).IsEqualTo(SubscriptionStatus.PastDue);

        // Verify an audit log entry was created for the cancellation request
        AuditLogEntry? auditEntry = await verifyDb.AuditLog
            .Where(a => a.TenantId == tenantId && a.Action == AuditAction.SubscriptionCancelRequested)
            .FirstOrDefaultAsync();
        await Assert.That(auditEntry).IsNotNull();
    }

    [Test]
    public async Task CancelSubscription_ActiveSubscription_ResponseContainsAllExpectedFields()
    {
        // Verify the full response payload structure matches the API contract
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Verify top-level ApiResponse fields exist
        await Assert.That(root.TryGetProperty("success", out _)).IsTrue();
        await Assert.That(root.TryGetProperty("data", out _)).IsTrue();

        // Verify nested CancelSubscriptionResponse fields exist
        JsonElement data = root.GetProperty("data");
        await Assert.That(data.TryGetProperty("success", out _)).IsTrue();
        await Assert.That(data.TryGetProperty("message", out _)).IsTrue();

        // Verify the outer success flag and inner success flag are both true
        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();
    }

    [Test]
    public async Task CancelSubscription_AlreadyCancelling_DoesNotModifyDatabaseTimestamp()
    {
        // When Stripe already reflects a pending cancellation, the endpoint should return
        // idempotently without writing to the database again
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        // Record the UpdatedAt timestamp before any cancel attempt
        TenantSubscription? originalSubscription = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();
        DateTimeOffset originalUpdatedAt = originalSubscription!.UpdatedAt;

        // Configure the billing mock to report that Stripe already has cancel_at_period_end set
        factory.BillingApiClientMock.GetSubscriptionStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                new StripeSubscriptionStatus(true, "active", string.Empty, 0, null, BillingTier.Unspecified)));

        // Cancel when Stripe already reflects cancellation
        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Verify the database timestamp was not modified
        using DatabaseContext verifyDb = factory.CreateDbContext();
        TenantSubscription? subscription = await verifyDb.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.UpdatedAt).IsEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task CancelSubscription_NoSubscription_Returns404()
    {
        // A tenant with no subscription record should receive a 404 response with
        // a non-success payload — the handler cannot proceed without a subscription record
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"No Sub Cancel Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = new()
        {
            ExternalId = $"ext-cancel-nosub-{Guid.NewGuid():N}",
            Username = $"cancel-nosub-{Guid.NewGuid():N}@example.com",
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
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        HttpClient client = BuildClient(factory, tenant.Id, user.Id);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsFalse();
    }

    [Test]
    public async Task CancelSubscription_FreeTier_WritesAuditLogEntryAtomically()
    {
        // Intent: for a Free-tier cancellation the subscription deactivation and audit log entry
        // must be written in the same transaction. This test confirms the transactional path still
        // records the audit row when the operation succeeds.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using DatabaseContext verifyDb = factory.CreateDbContext();

        TenantSubscription? subscription = await verifyDb.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();
        await Assert.That(subscription).IsNotNull();
        await Assert.That(subscription!.Status).IsEqualTo(SubscriptionStatus.Canceled);

        AuditLogEntry? auditEntry = await verifyDb.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Action == AuditAction.SubscriptionCancelRequested))
            .FirstOrDefaultAsync();
        await Assert.That(auditEntry).IsNotNull();
        await Assert.That(auditEntry!.ResourceType).IsEqualTo(AuditResourceType.Subscription);
    }

    [Test]
    public async Task CancelSubscription_FreeTier_CancelsImmediatelyWithoutStripe()
    {
        // Free-tier tenants have no Stripe subscription, so cancellation takes effect immediately
        // and the billing-api is never called — the handler deactivates the subscription directly
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedBillingEnvironment(db, tier: SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool outerSuccess = root.GetProperty("success").GetBoolean();
        await Assert.That(outerSuccess).IsTrue();

        JsonElement data = root.GetProperty("data");
        bool dataSuccess = data.GetProperty("success").GetBoolean();
        await Assert.That(dataSuccess).IsTrue();

        string message = data.GetProperty("message").GetString()!;
        await Assert.That(message).Contains("canceled");

        // The billing-api should not have been called for a free-tier cancellation
        await factory.BillingApiClientMock.DidNotReceive()
            .CancelSubscriptionAsync(Arg.Any<string>(), Arg.Any<PendingActionType>(), Arg.Any<CancellationToken>());
    }
}
