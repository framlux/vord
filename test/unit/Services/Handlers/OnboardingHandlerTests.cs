// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="OnboardingHandler"/>.
/// </summary>
public class OnboardingHandlerTests
{
    [Test]
    public async Task CreateOrganizationAsync_EmptyName_Returns400()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("required");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTooLong_Returns400()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());
        string longName = new('A', 101);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync(longName, "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateOrganizationAsync_ZeroUserId_Returns401()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_EmptyUniqueId_Returns401()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 1, "", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_UserAlreadyHasTenants_Returns409()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 1, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already belong");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTaken_Returns409()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByNameAsync("Existing Org", Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 99, Name = "Existing Org", ExternalId = "ext-99", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("Existing Org", "free", 1, "ext-1", CancellationToken.None);

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
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
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
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("New Org", "free", 1, "ext-1", CancellationToken.None);

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
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 5, FreeTierRetentionDays = 7 });
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
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateOrganizationAsync("New Org", "free", 1, "ext-1", CancellationToken.None);

        await subscriptionRepository.Received(1).CreateTenantSubscriptionAsync(
            Arg.Is<TenantSubscription>(s =>
                s.TenantId == 42 &&
                s.Tier == SubscriptionTier.Free &&
                s.Status == SubscriptionStatus.Active &&
                s.MachineLimit == 5 &&
                s.RetentionDays == 7),
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
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IOptions<SubscriptionOptions> subOptions = Options.Create(new SubscriptionOptions { FreeTierMachineLimit = 2, FreeTierRetentionDays = 1 });
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
        OnboardingHandler handler = new(transactionProvider, tenantRepository, subscriptionRepository, auditLog, subOptions, Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateOrganizationAsync("New Org", "free", 1, "ext-1", CancellationToken.None);

        await tenantRepository.Received(1).CreateUserTenantRoleAsync(
            Arg.Is<UserTenantRole>(r =>
                r.UserId == 1 &&
                r.AssignedTenantId == 42 &&
                r.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }
}
