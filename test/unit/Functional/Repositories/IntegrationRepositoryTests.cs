// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for integration-related methods on <see cref="DatabaseRepository"/>.
/// </summary>
public sealed class IntegrationRepositoryTests
{
    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Many integration tests require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    private static IntegrationEndpoint BuildIntegration(
        int tenantId,
        int createdByUserId,
        IntegrationProvider provider = IntegrationProvider.Custom,
        string? name = null,
        bool isEnabled = true,
        DateTimeOffset? deletedAt = null)
    {
        return new IntegrationEndpoint
        {
            TenantId = tenantId,
            Provider = provider,
            Name = name ?? "Test Integration",
            Configuration = """{"url":"https://hooks.example.com/test","secret":"encrypted-secret"}""",
            IsEnabled = isEnabled,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = deletedAt,
        };
    }

    [Test]
    public async Task CreateIntegrationAsync_ReturnsEntityWithIdSet()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, name: "My Integration");
        IntegrationEndpoint result = await repo.CreateIntegrationAsync(integration);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.Name).IsEqualTo("My Integration");
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
    }

    [Test]
    public async Task GetIntegrationsForTenantAsync_ReturnsOnlyNonDeletedForCorrectTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Active integration
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Active"));

        // Deleted integration (should be excluded)
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Deleted", deletedAt: DateTimeOffset.UtcNow));

        // Different tenant integration (should be excluded)
        Tenant tenant2 = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId2, userId, name: "Other Tenant"));

        List<IntegrationEndpoint> result = await repo.GetIntegrationsForTenantAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("Active");
    }

    [Test]
    public async Task GetIntegrationByIdAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        // Attempt to fetch with a different tenant ID
        IntegrationEndpoint? result = await repo.GetIntegrationByIdAsync(integration.Id, tenantId + 999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetIntegrationByIdAsync_DeletedIntegration_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, deletedAt: DateTimeOffset.UtcNow);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        IntegrationEndpoint? result = await repo.GetIntegrationByIdAsync(integration.Id, tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task SoftDeleteIntegrationAsync_SetsDeletedAt()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        int updated = await repo.SoftDeleteIntegrationAsync(integration.Id, tenantId, userId);

        await Assert.That(updated).IsEqualTo(1);

        // The integration should no longer be found by GetIntegrationByIdAsync
        IntegrationEndpoint? result = await repo.GetIntegrationByIdAsync(integration.Id, tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CountIntegrationsForTenantAsync_ExcludesDeleted()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Two active integrations
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Active 1"));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Active 2"));

        // One deleted integration
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Deleted", deletedAt: DateTimeOffset.UtcNow));

        int count = await repo.CountIntegrationsForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task GetEnabledIntegrationsForTenantAsync_ExcludesDisabledAndDeleted()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Enabled and active
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Enabled", isEnabled: true));

        // Disabled (should be excluded)
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Disabled", isEnabled: false));

        // Deleted (should be excluded)
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Deleted", isEnabled: true, deletedAt: DateTimeOffset.UtcNow));

        List<IntegrationEndpoint> result = await repo.GetEnabledIntegrationsForTenantAsync(tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("Enabled");
    }

    [Test]
    public async Task UpdateIntegrationEnabledAsync_UpdatesFlagAndUpdatedAt()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, isEnabled: true);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        await repo.UpdateIntegrationEnabledAsync(integration.Id, false);

        IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.IsEnabled).IsFalse();
        await Assert.That(updated.UpdatedAt).IsNotNull();
    }

    [Test]
    public async Task DisableIntegrationsForTenantAsync_DisablesAllEnabledIntegrations()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Two enabled integrations
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Enabled 1", isEnabled: true));
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Enabled 2", isEnabled: true));

        // One already disabled
        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, name: "Already Disabled", isEnabled: false));

        int updated = await repo.DisableIntegrationsForTenantAsync(tenantId);

        await Assert.That(updated).IsEqualTo(2);

        // Verify all are now disabled
        List<IntegrationEndpoint> all = await repo.GetIntegrationsForTenantAsync(tenantId);

        await Assert.That(all.All(i => i.IsEnabled == false)).IsTrue();
    }

    [Test]
    public async Task DisableIntegrationsForTenantAsync_NoEnabled_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await dbFactory.Context.InsertWithInt32IdentityAsync(
            BuildIntegration(tenantId, userId, isEnabled: false));

        int updated = await repo.DisableIntegrationsForTenantAsync(tenantId);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateIntegrationNameAsync_UpdatesName()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, name: "Original Name");
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        await repo.UpdateIntegrationNameAsync(integration.Id, "Updated Name");

        IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("Updated Name");
        await Assert.That(updated.UpdatedAt).IsNotNull();
    }

    [Test]
    public async Task UpdateIntegrationConfigurationAsync_UpdatesConfiguration()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        string newConfig = """{"url":"https://hooks.example.com/updated","secret":"new-encrypted-secret"}""";
        await repo.UpdateIntegrationConfigurationAsync(integration.Id, newConfig);

        IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Configuration).IsEqualTo(newConfig);
        await Assert.That(updated.UpdatedAt).IsNotNull();
    }

    [Test]
    public async Task UpdateIntegrationAsync_UpdatesMultipleFields()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, name: "Original", isEnabled: true);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        string newConfig = """{"url":"https://hooks.example.com/multi","secret":"updated-secret"}""";
        await repo.UpdateIntegrationAsync(integration.Id, "Multi Update", false, newConfig);

        IntegrationEndpoint? updated = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Name).IsEqualTo("Multi Update");
        await Assert.That(updated.IsEnabled).IsFalse();
        await Assert.That(updated.Configuration).IsEqualTo(newConfig);
        await Assert.That(updated.UpdatedAt).IsNotNull();
    }

    [Test]
    public async Task SoftDeleteIntegrationAsync_SetsDeletedByUserId()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        int deletingUserId = userId;
        await repo.SoftDeleteIntegrationAsync(integration.Id, tenantId, deletingUserId);

        // Query the raw record directly since GetIntegrationByIdAsync excludes deleted records
        IntegrationEndpoint? rawRecord = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(rawRecord).IsNotNull();
        await Assert.That(rawRecord!.DeletedAt).IsNotNull();
        await Assert.That(rawRecord.DeletedByUserId).IsEqualTo(deletingUserId);
    }

    [Test]
    public async Task SoftDeleteIntegrationAsync_WrongTenant_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Create a second tenant so we have a valid but different tenant ID
        Tenant otherTenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int otherTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(otherTenant);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        // Attempt to delete using the wrong tenant ID
        int rowsAffected = await repo.SoftDeleteIntegrationAsync(integration.Id, otherTenantId, userId);

        await Assert.That(rowsAffected).IsEqualTo(0);

        // Verify the integration is still accessible via the correct tenant
        IntegrationEndpoint? stillExists = await repo.GetIntegrationByIdAsync(integration.Id, tenantId);

        await Assert.That(stillExists).IsNotNull();
        await Assert.That(stillExists!.DeletedAt).IsNull();
    }

    [Test]
    public async Task UpdateIntegrationAsync_AllFieldsUpdatedWithSameTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        IIntegrationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        IntegrationEndpoint integration = BuildIntegration(tenantId, userId, name: "Original", isEnabled: true);
        integration.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(integration);

        string newConfig = """{"url":"https://new.example.com"}""";
        await repo.UpdateIntegrationAsync(integration.Id, "New Name", false, newConfig);

        // Bypass the repository's soft-delete filter to get the raw record
        IntegrationEndpoint? rawRecord = await dbFactory.Context.IntegrationEndpoints
            .FirstOrDefaultAsync(i => i.Id == integration.Id);

        await Assert.That(rawRecord).IsNotNull();
        await Assert.That(rawRecord!.Name).IsEqualTo("New Name");
        await Assert.That(rawRecord.IsEnabled).IsFalse();
        await Assert.That(rawRecord.Configuration).Contains("new.example.com");
        await Assert.That(rawRecord.UpdatedAt).IsNotNull();
    }
}
