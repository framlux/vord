// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for webhook-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class WebhookRepositoryTests
{
    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Many webhook tests require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    [Test]
    public async Task CreateWebhookAsync_ValidWebhook_ReturnsWebhookWithGeneratedId()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(
            tenantId: tenantId,
            name: "My Webhook",
            url: "https://hooks.example.com/alerts",
            createdByUserId: userId);

        WebhookEndpoint result = await repo.CreateWebhookAsync(webhook);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.Name).IsEqualTo("My Webhook");
        await Assert.That(result.Url).IsEqualTo("https://hooks.example.com/alerts");
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.IsEnabled).IsTrue();
    }

    [Test]
    public async Task GetWebhooksForTenantAsync_EmptyTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<WebhookEndpoint> result = await repo.GetWebhooksForTenantAsync(99999);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetWebhooksForTenantAsync_MultipleWebhooks_ReturnsAllOrderedByName()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhookC = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Charlie", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhookC);

        WebhookEndpoint webhookA = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Alpha", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhookA);

        WebhookEndpoint webhookB = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Bravo", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhookB);

        List<WebhookEndpoint> result = await repo.GetWebhooksForTenantAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Name).IsEqualTo("Alpha");
        await Assert.That(result[1].Name).IsEqualTo("Bravo");
        await Assert.That(result[2].Name).IsEqualTo("Charlie");
    }

    [Test]
    public async Task GetWebhooksForTenantAsync_DifferentTenants_ReturnsTenantIsolatedResults()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId1) = await SeedUserAndTenantAsync(dbFactory);

        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        WebhookEndpoint webhook1 = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId1, name: "Tenant1 Hook", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook1);

        WebhookEndpoint webhook2 = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId2, name: "Tenant2 Hook", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(webhook2);

        List<WebhookEndpoint> tenant1Results = await repo.GetWebhooksForTenantAsync(tenantId1);
        List<WebhookEndpoint> tenant2Results = await repo.GetWebhooksForTenantAsync(tenantId2);

        await Assert.That(tenant1Results.Count).IsEqualTo(1);
        await Assert.That(tenant1Results[0].Name).IsEqualTo("Tenant1 Hook");
        await Assert.That(tenant2Results.Count).IsEqualTo(1);
        await Assert.That(tenant2Results[0].Name).IsEqualTo("Tenant2 Hook");
    }

    [Test]
    public async Task GetWebhookByIdAsync_ExistingWebhook_ReturnsWebhook()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Lookup Hook", createdByUserId: userId);
        int webhookId = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        WebhookEndpoint? result = await repo.GetWebhookByIdAsync(webhookId, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(webhookId);
        await Assert.That(result.Name).IsEqualTo("Lookup Hook");
    }

    [Test]
    public async Task GetWebhookByIdAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        WebhookEndpoint? result = await repo.GetWebhookByIdAsync(99999, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetWebhookByIdAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, createdByUserId: userId);
        int webhookId = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        // Attempt to fetch the webhook with a different tenant ID
        WebhookEndpoint? result = await repo.GetWebhookByIdAsync(webhookId, tenantId + 999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task DeleteWebhookAsync_ExistingWebhook_ReturnsOneAndRemovesRecord()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, createdByUserId: userId);
        int webhookId = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        int deleted = await repo.DeleteWebhookAsync(webhookId);

        await Assert.That(deleted).IsEqualTo(1);

        // Verify the webhook no longer exists
        WebhookEndpoint? lookup = await repo.GetWebhookByIdAsync(webhookId, tenantId);

        await Assert.That(lookup).IsNull();
    }

    [Test]
    public async Task DeleteWebhookAsync_NonExistentWebhook_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int deleted = await repo.DeleteWebhookAsync(99999);

        await Assert.That(deleted).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateWebhookEnabledAsync_TogglesEnabledFlag()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, isEnabled: true, createdByUserId: userId);
        int webhookId = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        // Disable the webhook
        await repo.UpdateWebhookEnabledAsync(webhookId, false);

        WebhookEndpoint? disabled = await repo.GetWebhookByIdAsync(webhookId, tenantId);

        await Assert.That(disabled).IsNotNull();
        await Assert.That(disabled!.IsEnabled).IsFalse();

        // Re-enable the webhook
        await repo.UpdateWebhookEnabledAsync(webhookId, true);

        WebhookEndpoint? enabled = await repo.GetWebhookByIdAsync(webhookId, tenantId);

        await Assert.That(enabled).IsNotNull();
        await Assert.That(enabled!.IsEnabled).IsTrue();
    }

    [Test]
    public async Task UpdateWebhookSecretAsync_UpdatesSecret()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        WebhookEndpoint webhook = TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, secret: "original-secret", createdByUserId: userId);
        int webhookId = await dbFactory.Context.InsertWithInt32IdentityAsync(webhook);

        await repo.UpdateWebhookSecretAsync(webhookId, "new-encrypted-secret");

        WebhookEndpoint? updated = await repo.GetWebhookByIdAsync(webhookId, tenantId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Secret).IsEqualTo("new-encrypted-secret");
    }

    [Test]
    public async Task CountWebhooksForTenantAsync_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, createdByUserId: userId));

        int count = await repo.CountWebhooksForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountWebhooksForTenantAsync_TenantIsolation_DoesNotCountOtherTenants()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId1) = await SeedUserAndTenantAsync(dbFactory);

        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId1, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId2, createdByUserId: userId));

        int count1 = await repo.CountWebhooksForTenantAsync(tenantId1);
        int count2 = await repo.CountWebhooksForTenantAsync(tenantId2);

        await Assert.That(count1).IsEqualTo(1);
        await Assert.That(count2).IsEqualTo(1);
    }

    [Test]
    public async Task DisableWebhooksForTenantAsync_DisablesAllEnabledWebhooks()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, isEnabled: true, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, isEnabled: true, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, isEnabled: false, createdByUserId: userId));

        int updated = await repo.DisableWebhooksForTenantAsync(tenantId);

        await Assert.That(updated).IsEqualTo(2);

        // Verify all webhooks are now disabled
        List<WebhookEndpoint> webhooks = await repo.GetWebhooksForTenantAsync(tenantId);

        await Assert.That(webhooks.Count).IsEqualTo(3);
        await Assert.That(webhooks.All(w => w.IsEnabled == false)).IsTrue();
    }

    [Test]
    public async Task DisableWebhooksForTenantAsync_NoEnabledWebhooks_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, isEnabled: false, createdByUserId: userId));

        int updated = await repo.DisableWebhooksForTenantAsync(tenantId);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task GetEnabledWebhooksForTenantAsync_ReturnsOnlyEnabled()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Enabled Hook", isEnabled: true, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId, name: "Disabled Hook", isEnabled: false, createdByUserId: userId));

        List<WebhookEndpoint> result = await repo.GetEnabledWebhooksForTenantAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("Enabled Hook");
        await Assert.That(result[0].IsEnabled).IsTrue();
    }

    [Test]
    public async Task GetEnabledWebhooksForTenantAsync_TenantIsolation_DoesNotReturnOtherTenants()
    {
        using TestDatabaseFactory dbFactory = new();
        IWebhookRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId1) = await SeedUserAndTenantAsync(dbFactory);

        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId1, name: "T1 Enabled", isEnabled: true, createdByUserId: userId));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            TestDataBuilder.BuildWebhookEndpoint(tenantId: tenantId2, name: "T2 Enabled", isEnabled: true, createdByUserId: userId));

        List<WebhookEndpoint> tenant1Results = await repo.GetEnabledWebhooksForTenantAsync(tenantId1);

        await Assert.That(tenant1Results.Count).IsEqualTo(1);
        await Assert.That(tenant1Results[0].Name).IsEqualTo("T1 Enabled");
    }
}
