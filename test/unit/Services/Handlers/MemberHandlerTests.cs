// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using NSubstitute;

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(5, 1, 5, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("cannot remove yourself");
    }

    [Test]
    public async Task RemoveAsync_TargetNotFound_Returns404()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RemoveAsync_Success_Returns200()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.RemoveAsync(2, 1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Success).IsTrue();
    }

    // ========== ChangeRoleAsync tests ==========

    [Test]
    public async Task ChangeRoleAsync_NullTenantId_Returns401()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

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
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(5, 1, 5, "Viewer", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("cannot change your own role");
    }

    [Test]
    public async Task ChangeRoleAsync_TeamTier_Success_AssignsNewRole()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await tenantRepository.Received(1).CreateUserTenantRoleAsync(
            Arg.Is<UserTenantRole>(r => r.Role == UserAccountRoles.Viewer && r.UserId == 2 && r.AssignedTenantId == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChangeRoleAsync_TeamTier_TargetNotFound_Returns404()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.DisableUserTenantRoleAsync(2, 1, 1, Arg.Any<CancellationToken>()).Returns(false);
        ISubscriptionService subService = Substitute.For<ISubscriptionService>();
        subService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = SubscriptionTier.Team, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        MemberHandler handler = new(transactionProvider, auditLog, tenantRepository, subService, Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<ApiResponse<object>> result = await handler.ChangeRoleAsync(2, 1, 1, "Viewer", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }
}
