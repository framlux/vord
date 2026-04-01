// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
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
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("required");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTooLong_Returns400()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());
        string longName = new('A', 101);

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync(longName, "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateOrganizationAsync_ZeroUserId_Returns401()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_EmptyUniqueId_Returns401()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 1, "", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateOrganizationAsync_UserAlreadyHasTenants_Returns409()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 1, Role = Database.Enums.UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("My Org", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already belong");
    }

    [Test]
    public async Task CreateOrganizationAsync_NameTaken_Returns409()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.GetTenantByNameAsync("Existing Org", Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 99, Name = "Existing Org", ExternalId = "ext-99", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("Existing Org", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already exists");
    }

    [Test]
    public async Task CreateOrganizationAsync_Success_ReturnsTenantId()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        cache.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.GetTenantByNameAsync("New Org", Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 42;

            return t;
        });
        subService.ProvisionFreeSubscriptionAsync(42, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 42, Tier = SubscriptionTier.Free, Status = SubscriptionStatus.Active,
            RetentionDays = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<OnboardingResult> result = await handler.CreateOrganizationAsync("New Org", "free", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TenantId).IsEqualTo(42);
    }

    [Test]
    public async Task CreateOrganizationAsync_Success_AssignsUserAsTenantAdmin()
    {
        IDatabaseCache cache = Substitute.For<IDatabaseCache>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        cache.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.GetTenantByNameAsync("New Org", Arg.Any<CancellationToken>()).Returns((Tenant?)null);
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 42;

            return t;
        });
        subService.ProvisionFreeSubscriptionAsync(42, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 42, Tier = SubscriptionTier.Free, Status = SubscriptionStatus.Active,
            RetentionDays = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        OnboardingHandler handler = new(cache, subService, Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateOrganizationAsync("New Org", "free", 1, "ext-1", CancellationToken.None);

        await cache.Received(1).CreateUserTenantRoleAsync(Arg.Is<UserTenantRole>(r => r.UserId == 1 && r.AssignedTenantId == 42), Arg.Any<CancellationToken>());
    }
}
