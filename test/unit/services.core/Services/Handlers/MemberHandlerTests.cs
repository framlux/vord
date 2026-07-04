// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;
using NSubstitute;
using System.Data;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="MemberHandler"/>.
/// </summary>
public class MemberHandlerTests
{
    [Test]
    public async Task RemoveAsync_NullTenantId_Returns401()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, null, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task RemoveAsync_SelfRemoval_Returns400()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(5, 1, 5, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("cannot remove yourself");
    }

    [Test]
    public async Task RemoveAsync_TargetNotFound_Returns404()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RemoveAsync_Success_Returns200()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Success).IsTrue();
        await roleCacheInvalidator.Received(1).InvalidateAsync(2, Arg.Any<CancellationToken>());
        await roleCacheInvalidator.Received(1).InvalidateUserStateAsync(2, Arg.Any<CancellationToken>());
        await stampService.Received(1).BumpAsync(2, Arg.Any<CancellationToken>());
    }

    // ========== ChangeRoleAsync tests ==========

    [Test]
    public async Task ChangeRoleAsync_NullTenantId_Returns401()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, null, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task ChangeRoleAsync_NoSubscription_Returns403()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns((TenantSubscription?)null);
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data!.Message).Contains("Team subscription");
    }

    [Test]
    public async Task ChangeRoleAsync_ProTier_Returns403()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Pro, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data!.Message).Contains("Team subscription");
    }

    [Test]
    public async Task ChangeRoleAsync_InvalidRoleString_Returns400()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "NotARealRole", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("Invalid role");
    }

    [Test]
    public async Task ChangeRoleAsync_SelfRoleChange_Returns400()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(5, 1, 5, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("cannot change your own role");
    }

    [Test]
    public async Task ChangeRoleAsync_TeamTier_Success_AssignsNewRole()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await tenantRepository.Received(1).CreateUserTenantRoleAsync(
            Arg.Is<UserTenantRole>(r => r.Role == UserAccountRoles.Viewer && r.UserId == 2 && r.AssignedTenantId == 1),
            Arg.Any<CancellationToken>());
        await roleCacheInvalidator.Received(1).InvalidateAsync(2, Arg.Any<CancellationToken>());
        await roleCacheInvalidator.Received(1).InvalidateUserStateAsync(2, Arg.Any<CancellationToken>());
        await stampService.Received(1).BumpAsync(2, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangeRoleAsync_TeamTier_TargetNotFound_Returns404()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    // ========== Last non-OIDC TenantAdmin guard tests ==========

    [Test]
    public async Task RemoveAsync_LastNonOidcAdmin_Returns409AndDoesNotCommit()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Data!.Message).Contains("last administrator");

        // The guard rejects after the disable write, so the transaction must never commit
        // (dispose-without-commit rolls the disable back) and no post-commit side effects run.
        await mockTransaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await roleCacheInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await roleCacheInvalidator.DidNotReceive().InvalidateUserStateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await stampService.DidNotReceive().BumpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveAsync_AnotherNonOidcAdminRemains_Returns200AndCommits()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await mockTransaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangeRoleAsync_DemotesLastNonOidcAdmin_Returns409AndDoesNotCommit()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Data!.Message).Contains("last administrator");

        // The guard runs after both role writes, so the change must never commit and no
        // post-commit side effects run.
        await mockTransaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await roleCacheInvalidator.DidNotReceive().InvalidateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await roleCacheInvalidator.DidNotReceive().InvalidateUserStateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await stampService.DidNotReceive().BumpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangeRoleAsync_DemotesAdminWhenAnotherRemains_Returns200AndCommits()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        IRoleCacheInvalidator roleCacheInvalidator = Substitute.For<IRoleCacheInvalidator>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, roleCacheInvalidator, stampService);

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await mockTransaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    // ========== Guard is scoped to admin mutations (regression) ==========

    [Test]
    public async Task RemoveAsync_NonAdminMember_SkipsGuardAndSucceeds()
    {
        // An all-SSO Team tenant has no non-CustomOidc admin, so the guard would misfire on every
        // removal. Removing a Viewer must succeed without ever consulting the guard.
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.Viewer);
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        // Simulate an all-SSO tenant: no non-CustomOidc admin. This must never be consulted.
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await mockTransaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await tenantRepository.DidNotReceive().HasNonOidcTenantAdminAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangeRoleAsync_NonAdminMember_SkipsGuardAndSucceeds()
    {
        // Demoting a non-admin (MachineAdmin -> Viewer) in an all-SSO tenant must not trip the guard.
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.MachineAdmin);
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await tenantRepository.DidNotReceive().HasNonOidcTenantAdminAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveAsync_UsesSerializableIsolation()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.GetActiveUserRoleAsync(2, 1, Arg.Any<CancellationToken>()).Returns(UserAccountRoles.TenantAdmin);
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        tenantRepository.HasNonOidcTenantAdminAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>(), Substitute.For<IUserSecurityStampService>());

        await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await transactionProvider.Received().BeginTransactionAsync(IsolationLevel.Serializable, Arg.Any<CancellationToken>());
    }
}
