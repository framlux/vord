// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="OnboardingHandler"/>.
/// </summary>
public class OnboardingHandlerTests
{
    private static OnboardingHandler BuildHandler(
        IDatabaseTransactionProvider? transactionProvider = null,
        ITenantRepository? tenantRepository = null,
        ISubscriptionRepository? subscriptionRepository = null,
        IAuditLogRepository? auditLog = null,
        IRoleCacheInvalidator? roleCacheInvalidator = null)
    {
        return new OnboardingHandler(
            transactionProvider ?? Substitute.For<IDatabaseTransactionProvider>(),
            tenantRepository ?? Substitute.For<ITenantRepository>(),
            subscriptionRepository ?? Substitute.For<ISubscriptionRepository>(),
            auditLog ?? Substitute.For<IAuditLogRepository>(),
            roleCacheInvalidator ?? Substitute.For<IRoleCacheInvalidator>());
    }

    [Test]
    public async Task CreateOrganizationAsync_EmptyName_Returns400()
    {
        OnboardingHandler handler = BuildHandler();

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("required");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTooLong_Returns400()
    {
        OnboardingHandler handler = BuildHandler();
        string longName = new('A', 101);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync(longName, 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateOrganizationAsync_ZeroUserId_Returns401()
    {
        OnboardingHandler handler = BuildHandler();

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_EmptyUniqueId_Returns401()
    {
        OnboardingHandler handler = BuildHandler();

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", 1, "", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_UserAlreadyHasTenants_Returns409()
    {
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 1, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        OnboardingHandler handler = BuildHandler(tenantRepository: tenantRepository);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already belong");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTaken_Returns409()
    {
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByNameAsync("Existing Org", Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 99, Name = "Existing Org", ExternalId = "ext-99", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        OnboardingHandler handler = BuildHandler(tenantRepository: tenantRepository);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("Existing Org", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already exists");
    }

    [Test]
    public async Task CreateOrganizationAsync_Success_ReturnsTenantId()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByNameAsync("New Org", Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 42;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        OnboardingHandler handler = BuildHandler(
            transactionProvider: transactionProvider,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("New Org", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TenantId).IsEqualTo(42);
    }

    [Test]
    public async Task CreateOrganizationAsync_Success_CreatesSubscriptionViaSubscriptionRepository()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByNameAsync("New Org", Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 42;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        OnboardingHandler handler = BuildHandler(
            transactionProvider: transactionProvider,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

        await handler.CreateOrganizationAsync("New Org", 1, "ext-1", CancellationToken.None);

        await subscriptionRepository.Received(1).CreateTenantSubscriptionAsync(
            Arg.Is<TenantSubscription>(s =>
                s.TenantId == 42 &&
                s.Tier == SubscriptionTier.Free &&
                s.Status == SubscriptionStatus.Active),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateOrganizationAsync_Success_AssignsUserAsTenantAdmin()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByNameAsync("New Org", Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 42;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        OnboardingHandler handler = BuildHandler(
            transactionProvider: transactionProvider,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

        await handler.CreateOrganizationAsync("New Org", 1, "ext-1", CancellationToken.None);

        await tenantRepository.Received(1).CreateUserTenantRoleAsync(
            Arg.Is<UserTenantRole>(r =>
                r.UserId == 1 &&
                r.AssignedTenantId == 42 &&
                r.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }
}
