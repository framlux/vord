// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Notifications;
using Framlux.FleetManagement.Server.Services.Security;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="InvitationHandler"/>.
/// </summary>
public class InvitationHandlerTests
{
    private static (
        IDatabaseTransactionProvider TransactionProvider,
        IAuditLogRepository AuditLog,
        IInvitationRepository InvitationRepository,
        ITenantRepository TenantRepository,
        ISubscriptionRepository SubscriptionRepository
    ) CreateMockRepositories()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IInvitationRepository invitationRepository = Substitute.For<IInvitationRepository>();
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();

        return (transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository);
    }

    private static IEmailService CreateMockEmailService()
    {
        IEmailService svc = Substitute.For<IEmailService>();
        svc.SendInvitationEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        return svc;
    }

    private static ISubscriptionService CreateMockSubService(SubscriptionTier tier = SubscriptionTier.Pro)
    {
        ISubscriptionService svc = Substitute.For<ISubscriptionService>();
        svc.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = tier, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        return svc;
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_InvalidEmail_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("notanemail", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("valid email");
    }

    [Test]
    public async Task CreateAsync_EmptyEmail_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NullTenantId_Returns401()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, null, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateAsync_FreeTierSubscription_Returns402()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Free), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(402);
        await Assert.That(result.Data!.ErrorMessage).Contains("Upgrade");
    }

    [Test]
    public async Task CreateAsync_ExistingPendingInvitation_Returns409()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>())
            .Returns(new TenantInvitation { Id = 1, Email = "user@example.com", TenantId = 1, TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("pending invitation already exists");
    }

    [Test]
    public async Task CreateAsync_AlreadyMember_Returns409()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        UserAccount memberUser = new() { Id = 5, ExternalId = "ext-5", Username = "user@example.com", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 0, IsActive = true, IsSystem = false, IsGlobalAdmin = false };
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 5, User = memberUser, AssignedTenantId = 1, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task CreateAsync_Success_ReturnsInvitationData()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        IEmailService emailService = CreateMockEmailService();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, emailService, CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(10);
        await Assert.That(result.Data!.Email).IsEqualTo("newuser@example.com");
        await Assert.That(result.Data!.AcceptUrl).Contains("token=");
    }

    [Test]
    public async Task CreateAsync_Success_SendsInvitationEmail()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        IEmailService emailService = CreateMockEmailService();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, emailService, CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await emailService.Received(1).SendInvitationEmailAsync("newuser@example.com", "Test Org", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== CreateAsync role-forcing tests ==========

    [Test]
    public async Task CreateAsync_ProTier_RequestsViewer_ForcesToTenantAdmin()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Pro), Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_RequestsViewer_HonorsRequestedRole()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Team), Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_ProTier_NullRole_DefaultsToTenantAdmin()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Pro), Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_NullRole_DefaultsToViewer()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Team), Substitute.For<IRoleCacheInvalidator>());

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    // ========== AcceptAsync tests ==========

    [Test]
    public async Task AcceptAsync_TokenNotFound_Returns404()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("badtoken", Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("badtoken", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task AcceptAsync_AlreadyAccepted_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("already been");
    }

    [Test]
    public async Task AcceptAsync_Expired_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-8), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("expired");
    }

    [Test]
    public async Task AcceptAsync_EmailMismatch_Returns403()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "invited@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "wrong@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data!.ErrorMessage).Contains("does not match");
    }

    [Test]
    public async Task AcceptAsync_ZeroUserId_Returns401()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task AcceptAsync_AlreadyMember_Returns409()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 5, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task AcceptAsync_NewUser_ReturnsSuccessWithPersonalTenantFlag()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TenantId).IsEqualTo(5);
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsTrue();
    }

    [Test]
    public async Task AcceptAsync_NewUser_CreatesSubscriptionViaSubscriptionRepository()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await subscriptionRepository.Received(1).CreateTenantSubscriptionAsync(
            Arg.Is<TenantSubscription>(s =>
                s.TenantId == 99 &&
                s.Tier == SubscriptionTier.Free &&
                s.Status == SubscriptionStatus.Active),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptAsync_NewUser_CreatesTwoTenantRoleAssignments()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // Should have created personal tenant role + invitation tenant role = 2 calls
        await tenantRepository.Received(2).CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_ReturnsSuccessWithoutPersonalTenant()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsFalse();
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_CreatesOneTenantRoleAssignment()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // Only 1 call for the invitation tenant role (no personal tenant)
        await tenantRepository.Received(1).CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
    }

    // ========== RevokeAsync tests ==========

    [Test]
    public async Task RevokeAsync_NullTenantId_Returns401()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task RevokeAsync_InvitationNotFound_Returns404()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(99, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_NotPending_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("pending");
    }

    [Test]
    public async Task RevokeAsync_Success_ReturnsSuccess()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_Success_CallsRevokeOnRepository()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.RevokeAsync(1, 1, CancellationToken.None);

        await invitationRepository.Received(1).RevokeInvitationAsync(1, Arg.Any<CancellationToken>());
    }

    // ========== ResendAsync tests ==========

    [Test]
    public async Task ResendAsync_NullTenantId_Returns401()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, null, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task ResendAsync_InvitationNotFound_Returns404()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(99, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task ResendAsync_NotPending_Returns400()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Revoked, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, CreateMockEmailService(), CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task ResendAsync_Success_ReturnsNewInvitationData()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        IEmailService emailService = CreateMockEmailService();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "oldtoken", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 20;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, emailService, CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(20);
        await Assert.That(result.Data!.Email).IsEqualTo("user@test.com");
    }

    [Test]
    public async Task ResendAsync_Success_RevokesOldAndSendsEmail()
    {
        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog, IInvitationRepository invitationRepository, ITenantRepository tenantRepository, ISubscriptionRepository subscriptionRepository) = CreateMockRepositories();
        IEmailService emailService = CreateMockEmailService();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "oldtoken", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 20;

            return inv;
        });
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(transactionProvider, auditLog, invitationRepository, tenantRepository, subscriptionRepository, emailService, CreateMockSubService(), Substitute.For<IRoleCacheInvalidator>());

        await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).RevokeInvitationAsync(1, Arg.Any<CancellationToken>());
        await emailService.Received(1).SendInvitationEmailAsync("user@test.com", "Test Org", "inviter@test.com", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
