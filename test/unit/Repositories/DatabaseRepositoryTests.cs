// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.QueryCache;

/// <summary>
/// Intent-asserting tests for <see cref="DatabaseRepository"/> input validation.
/// These tests verify that invalid inputs are rejected with appropriate exceptions
/// rather than causing silent failures, NullReferenceExceptions, or data corruption.
/// </summary>
public class DatabaseRepositoryTests
{
    // ========== Constructor tests ==========

    [Test]
    public async Task Constructor_NullDatabaseContext_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
            new DatabaseRepository(null!, Substitute.For<ILogger<DatabaseRepository>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();

        await Assert.That(() =>
            new DatabaseRepository(dbFactory.Context, null!))
            .Throws<ArgumentNullException>();
    }

    // ========== Null string input tests — query methods ==========

    [Test]
    public async Task GetTenantsForUserAsync_NullId_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantsForUserAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetTenantByExternalIdAsync_NullId_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantByExternalIdAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetTenantByNameAsync_NullName_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantByNameAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetTenantOidcConfigByEmailDomainAsync_NullDomain_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantOidcConfigurationByEmailDomainAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetUserByExternalIdAsync_NullId_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetUserByExternalIdAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetUserByEmailAsync_NullEmail_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetUserByEmailAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetInvitationByTokenAsync_NullToken_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetInvitationByTokenAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetPendingInvitationByEmailAndTenantAsync_NullEmail_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetPendingInvitationByEmailAndTenantAsync(null!, 1, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetMachineByApiKeyAsync_NullKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetMachineByApiKeyAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task DoesMachineExistAsync_NullSerialNumber_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.DoesMachineExistAsync(null!, "system-id", "asset-tag", 1, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    // ========== Empty/whitespace string input tests — query methods ==========

    [Test]
    public async Task GetTenantByNameAsync_EmptyName_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantByNameAsync("", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetTenantByNameAsync_WhitespaceName_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetTenantByNameAsync("   ", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetUserByEmailAsync_EmptyEmail_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetUserByEmailAsync("", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetMachineByApiKeyAsync_EmptyKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetMachineByApiKeyAsync("", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetInvitationByTokenAsync_EmptyToken_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.GetInvitationByTokenAsync("", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task DoesMachineExistAsync_EmptySerialNumber_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.DoesMachineExistAsync("", "system-id", "asset-tag", 1, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    // ========== Null object input tests — create/mutate methods ==========

    [Test]
    public async Task CreateTenantAsync_NullTenant_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.CreateTenantAsync(null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CreateUserAccountAsync_NullUser_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.CreateUserAccountAsync(null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_NullMachine_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.CreateMachineWithKeyAsync(null!, null, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CreateInvitationAsync_NullInvitation_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.CreateInvitationAsync(null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CreateUserTenantRoleAsync_NullRole_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.CreateUserTenantRoleAsync(null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    // ========== Data integrity tests — create/mutate methods ==========

    [Test]
    public async Task UpdateUserEmailAsync_NullEmail_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.UpdateUserEmailAsync(1, null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task UpdateUserEmailAsync_EmptyEmail_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        await Assert.That(async () =>
            await cache.UpdateUserEmailAsync(1, "", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CreateTenantAsync_EmptyName_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Name = "";

        await Assert.That(async () =>
            await cache.CreateTenantAsync(tenant, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CreateTenantAsync_WhitespaceName_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Name = "   ";

        await Assert.That(async () =>
            await cache.CreateTenantAsync(tenant, CancellationToken.None))
            .Throws<ArgumentException>();
    }
}
