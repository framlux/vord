// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Tenants;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="TenantHandler"/>.
/// </summary>
public class TenantHandlerTests
{
    // ========== Constructor tests ==========

    [Test]
    public async Task Constructor_NullDatabaseCache_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();

        await Assert.That(() =>
            new TenantHandler(null!, dbFactory.Context, logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDatabaseContext_ThrowsArgumentNullException()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();

        await Assert.That(() =>
            new TenantHandler(cache, null!, logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        using TestDatabaseFactory dbFactory = new();

        await Assert.That(() =>
            new TenantHandler(cache, dbFactory.Context, null!))
            .Throws<ArgumentNullException>();
    }

    // ========== ListForUserAsync null input tests ==========

    [Test]
    public async Task ListForUserAsync_NullTenantIds_ThrowsArgumentNullException()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        await Assert.That(async () =>
            await handler.ListForUserAsync(false, null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_EmptyName_Returns400()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.CreateAsync("", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_WhitespaceName_Returns400()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.CreateAsync("   ", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_DuplicateName_Returns409()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.GetTenantByNameAsync("Existing Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(TestDataBuilder.BuildTenant(name: "Existing Corp")));
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.CreateAsync("Existing Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task CreateAsync_ValidName_ReturnsTenantDto()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.GetTenantByNameAsync("New Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "New Corp");
        createdTenant.Id = 42;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.CreateAsync("New Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Name).IsEqualTo("New Corp");
        await Assert.That(result.Data!.Id).IsEqualTo(42);
    }

    [Test]
    public async Task CreateAsync_ValidName_CallsCreateTenantOnCache()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.GetTenantByNameAsync("Call Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "Call Corp");
        createdTenant.Id = 10;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        await handler.CreateAsync("Call Corp", "https://logo.png", 5, CancellationToken.None);

        await cache.Received(1).CreateTenantAsync(Arg.Is<Tenant>(t => t.Name == "Call Corp"), Arg.Any<CancellationToken>());
    }

    // ========== GetDetailAsync tests ==========

    [Test]
    public async Task GetDetailAsync_TenantNotFound_ReturnsNotFound()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        cache.GetTenantByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.GetDetailAsync(999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task GetDetailAsync_ValidTenant_ReturnsTenantDto()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        Tenant tenant = TestDataBuilder.BuildTenant(name: "Detail Corp");
        tenant.Id = 7;
        cache.GetTenantByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(tenant));
        using TestDatabaseFactory dbFactory = new();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<TenantDto> result = await handler.GetDetailAsync(7, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Id).IsEqualTo(7);
        await Assert.That(result.Data!.Name).IsEqualTo("Detail Corp");
    }

    // ========== ListForUserAsync tests ==========

    [Test]
    public async Task ListForUserAsync_GlobalAdmin_ReturnsAllTenants()
    {
        using TestDatabaseFactory dbFactory = new();
        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Alpha Corp");
        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Beta Corp");
        tenant1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        tenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(true, [], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ListForUserAsync_NonAdmin_ReturnsOnlyMemberTenants()
    {
        using TestDatabaseFactory dbFactory = new();
        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Member Corp");
        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Other Corp");
        tenant1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        tenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(false, [tenant1.Id], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Name).IsEqualTo("Member Corp");
    }

    [Test]
    public async Task ListForUserAsync_NoTenants_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ILogger<TenantHandler> logger = Substitute.For<ILogger<TenantHandler>>();
        TenantHandler handler = new(cache, dbFactory.Context, logger);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(false, [], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }
}
