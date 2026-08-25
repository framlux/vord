// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Tenants;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="TenantHandler"/>.
/// </summary>
public class TenantHandlerTests
{
    private static TenantHandler BuildHandler(
        ITenantRepository? tenantRepository = null,
        IDatabaseTransactionProvider? transactionProvider = null,
        IAuditLogRepository? auditLog = null,
        ILogger<TenantHandler>? logger = null,
        ISubscriptionRepository? subscriptionRepository = null)
    {
        return new TenantHandler(
            tenantRepository ?? Substitute.For<ITenantRepository>(),
            subscriptionRepository ?? Substitute.For<ISubscriptionRepository>(),
            transactionProvider ?? Substitute.For<IDatabaseTransactionProvider>(),
            auditLog ?? Substitute.For<IAuditLogRepository>(),
            logger ?? Substitute.For<ILogger<TenantHandler>>());
    }

    // ========== ListForUserAsync null input tests ==========

    [Test]
    public async Task ListForUserAsync_NullTenantIds_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);
        // tenantRepository is intentionally left as the builder's default unconfigured mock: the
        // assertion below must be satisfied by the handler's own ArgumentNullException.ThrowIfNull
        // guard, not by whatever the real repository would do with a null list. Using `repo` here
        // would let this test keep passing even if the handler's guard were ever removed.
        TenantHandler handler = BuildHandler(transactionProvider: repo, auditLog: repo);

        await Assert.That(async () =>
            await handler.ListForUserAsync(false, null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_EmptyName_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_WhitespaceName_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("   ", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NameTooShort_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("Abcd", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NameTooLong_Returns400()
    {
        TenantHandler handler = BuildHandler();

        string longName = new('A', 101);
        ServiceResult<TenantDto> result = await handler.CreateAsync(longName, "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NameWithHtmlTags_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("<script>alert</script>", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NameWithControlChars_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("Test\x00Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NameWithBackslash_Returns400()
    {
        TenantHandler handler = BuildHandler();

        ServiceResult<TenantDto> result = await handler.CreateAsync("Test\\Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_UnicodeJapaneseName_Succeeds()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("テスト組織です", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "テスト組織です");
        createdTenant.Id = 50;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        TenantHandler handler = BuildHandler(tenantRepository: cache, transactionProvider: txProvider, auditLog: auditLog);

        ServiceResult<TenantDto> result = await handler.CreateAsync("テスト組織です", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task CreateAsync_NameWithLeadingTrailingWhitespace_IsTrimmed()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("Trimmed Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "Trimmed Corp");
        createdTenant.Id = 51;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        TenantHandler handler = BuildHandler(tenantRepository: cache, transactionProvider: txProvider, auditLog: auditLog);

        ServiceResult<TenantDto> result = await handler.CreateAsync("  Trimmed Corp  ", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await cache.Received(1).GetTenantByNameAsync("Trimmed Corp", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_NameWithValidSpecialChars_Succeeds()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("Acme & Co. - HQ_1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "Acme & Co. - HQ_1");
        createdTenant.Id = 52;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        TenantHandler handler = BuildHandler(tenantRepository: cache, transactionProvider: txProvider, auditLog: auditLog);

        ServiceResult<TenantDto> result = await handler.CreateAsync("Acme & Co. - HQ_1", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task CreateAsync_DuplicateName_Returns409()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("Existing Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(TestDataBuilder.BuildTenant(name: "Existing Corp")));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        TenantHandler handler = BuildHandler(tenantRepository: cache, transactionProvider: txProvider, auditLog: auditLog);

        ServiceResult<TenantDto> result = await handler.CreateAsync("Existing Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task CreateAsync_ValidName_ReturnsTenantDto()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("New Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "New Corp");
        createdTenant.Id = 42;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        TenantHandler handler = BuildHandler(tenantRepository: cache, transactionProvider: txProvider, auditLog: auditLog);

        ServiceResult<TenantDto> result = await handler.CreateAsync("New Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Name).IsEqualTo("New Corp");
        await Assert.That(result.Data!.Id).IsEqualTo(42);
    }

    // ========== GetDetailAsync tests ==========

    [Test]
    public async Task GetDetailAsync_TenantNotFound_ReturnsNotFound()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        TenantHandler handler = BuildHandler(tenantRepository: cache);

        ServiceResult<TenantDto> result = await handler.GetDetailAsync(999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_ValidTenant_ReturnsTenantDto()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        Tenant tenant = TestDataBuilder.BuildTenant(name: "Detail Corp");
        tenant.Id = 7;
        cache.GetTenantByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(tenant));
        TenantHandler handler = BuildHandler(tenantRepository: cache);

        ServiceResult<TenantDto> result = await handler.GetDetailAsync(7, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
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

        DatabaseRepository repo = CreateRepo(dbFactory);
        TenantHandler handler = BuildHandler(tenantRepository: repo, transactionProvider: repo, auditLog: repo);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(true, [], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
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

        DatabaseRepository repo = CreateRepo(dbFactory);
        TenantHandler handler = BuildHandler(tenantRepository: repo, transactionProvider: repo, auditLog: repo);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(false, [tenant1.Id], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Name).IsEqualTo("Member Corp");
    }

    [Test]
    public async Task ListForUserAsync_NoTenants_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = CreateRepo(dbFactory);
        TenantHandler handler = BuildHandler(tenantRepository: repo, transactionProvider: repo, auditLog: repo);

        ServiceResult<List<TenantDto>> result = await handler.ListForUserAsync(false, [], CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    private static (IDatabaseTransactionProvider, IAuditLogRepository) CreateMockTransactionAndAudit()
    {
        IDatabaseTransaction tx = Substitute.For<IDatabaseTransaction>();
        tx.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        IDatabaseTransactionProvider txProvider = Substitute.For<IDatabaseTransactionProvider>();
        txProvider.BeginTransactionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tx));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        return (txProvider, auditLog);
    }

    /// <summary>
    /// Every entitlement gate refuses a tenant with no subscription row, and the mutation gate now
    /// fails closed on one, so an admin-created tenant without a row would be joinable but unable
    /// to change anything. The other two tenant-creation paths have always provisioned; this is the
    /// one that did not.
    /// </summary>
    [Test]
    public async Task CreateAsync_ProvisionsFreeSubscriptionForTheNewTenant()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("Provisioned Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "Provisioned Corp");
        createdTenant.Id = 77;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));
        (IDatabaseTransactionProvider txProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        TenantHandler handler = BuildHandler(
            tenantRepository: cache,
            transactionProvider: txProvider,
            auditLog: auditLog,
            subscriptionRepository: subscriptions);

        ServiceResult<TenantDto> result = await handler.CreateAsync(
            "Provisioned Corp", "https://logo.png", 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await subscriptions.Received(1).CreateTenantSubscriptionAsync(
            Arg.Is<TenantSubscription>(s =>
                (s.TenantId == 77) &&
                (s.Tier == SubscriptionTier.Free) &&
                (s.Status == SubscriptionStatus.Active)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The subscription is written before the commit, so a failure to provision rolls the tenant
    /// back with it rather than leaving the row-less tenant this exists to prevent.
    /// </summary>
    [Test]
    public async Task CreateAsync_WhenProvisioningFails_DoesNotCommit()
    {
        ITenantRepository cache = Substitute.For<ITenantRepository>();
        cache.GetTenantByNameAsync("Rollback Corp", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Tenant?>(null));
        Tenant createdTenant = TestDataBuilder.BuildTenant(name: "Rollback Corp");
        createdTenant.Id = 78;
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTenant));

        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider txProvider = Substitute.For<IDatabaseTransactionProvider>();
        txProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>())
            .Returns<Task<TenantSubscription>>(_ => throw new InvalidOperationException("provisioning failed"));

        TenantHandler handler = BuildHandler(
            tenantRepository: cache,
            transactionProvider: txProvider,
            auditLog: Substitute.For<IAuditLogRepository>(),
            subscriptionRepository: subscriptions);

        await Assert.That(async () => await handler.CreateAsync(
                "Rollback Corp", "https://logo.png", 1, CancellationToken.None))
            .Throws<InvalidOperationException>();

        await transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }
}
