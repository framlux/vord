// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Security;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="InvitationHandler"/>.
/// </summary>
public class InvitationHandlerTests
{
    private static InvitationHandler BuildHandler(
        IDatabaseTransactionProvider? transactionProvider = null,
        IAuditLogRepository? auditLog = null,
        IInvitationRepository? invitationRepository = null,
        ITenantRepository? tenantRepository = null,
        ISubscriptionRepository? subscriptionRepository = null,
        IBackgroundJobClient? backgroundJobClient = null,
        ISubscriptionService? subscriptionService = null,
        IRoleCacheInvalidator? roleCacheInvalidator = null)
    {
        return new InvitationHandler(
            transactionProvider ?? CreateDefaultTransactionProvider(),
            auditLog ?? Substitute.For<IAuditLogRepository>(),
            invitationRepository ?? CreateDefaultInvitationRepository(),
            tenantRepository ?? CreateDefaultTenantRepository(),
            subscriptionRepository ?? Substitute.For<ISubscriptionRepository>(),
            backgroundJobClient ?? Substitute.For<IBackgroundJobClient>(),
            subscriptionService ?? CreateMockSubService(),
            roleCacheInvalidator ?? Substitute.For<IRoleCacheInvalidator>());
    }

    private static IDatabaseTransactionProvider CreateDefaultTransactionProvider()
    {
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IDatabaseTransaction mockTransaction = Substitute.For<IDatabaseTransaction>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(mockTransaction);

        return transactionProvider;
    }

    /// <summary>
    /// Defaults the tenant-scoped invitation writes to report a matching row so happy-path tests do
    /// not have to opt in; not-found tests override these to false.
    /// </summary>
    private static IInvitationRepository CreateDefaultInvitationRepository()
    {
        IInvitationRepository invitationRepository = Substitute.For<IInvitationRepository>();
        invitationRepository.RevokeInvitationAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        invitationRepository.UpdateInvitationStatusAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<InvitationStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);

        return invitationRepository;
    }

    /// <summary>
    /// Defaults the serializable member-limit insert to succeed so happy-path acceptance tests do
    /// not have to opt in; the at-limit test overrides this to false.
    /// </summary>
    private static ITenantRepository CreateDefaultTenantRepository()
    {
        ITenantRepository tenantRepository = Substitute.For<ITenantRepository>();
        tenantRepository.CreateUserTenantRoleWithMemberLimitAsync(Arg.Any<UserTenantRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);

        return tenantRepository;
    }

    private static ISubscriptionService CreateMockSubService(SubscriptionTier tier = SubscriptionTier.Pro, bool canAddMember = true, int memberLimit = 5)
    {
        ISubscriptionService svc = Substitute.For<ISubscriptionService>();
        svc.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 1, Tier = tier, Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        svc.CanAddMemberAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(canAddMember);
        svc.GetEffectiveLimitsForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new EffectiveLimits
        {
            MemberLimit = memberLimit,
        });

        return svc;
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_InvalidEmail_Returns400()
    {
        InvitationHandler handler = BuildHandler();

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("notanemail", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("valid email");
    }

    [Test]
    public async Task CreateAsync_EmptyEmail_Returns400()
    {
        InvitationHandler handler = BuildHandler();

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = BuildHandler();

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("user@example.com", null, null, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateAsync_FreeTierSubscription_Returns402()
    {
        InvitationHandler handler = BuildHandler(subscriptionService: CreateMockSubService(SubscriptionTier.Free));

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(402);
        await Assert.That(result.Data!.ErrorMessage).Contains("Upgrade");
    }

    [Test]
    public async Task CreateAsync_ExistingPendingInvitation_Returns409()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>())
            .Returns(new TenantInvitation { Id = 1, Email = "user@example.com", TenantId = 1, TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("pending invitation already exists");
    }

    [Test]
    public async Task CreateAsync_AlreadyMember_Returns409()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        UserAccount memberUser = new() { Id = 5, ExternalId = "ext-5", Username = "user@example.com", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 0, IsActive = true, IsSystem = false, IsGlobalAdmin = false };
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 5, User = memberUser, AssignedTenantId = 1, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task CreateAsync_MemberLimitReached_Returns409()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(canAddMember: false));

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("member limit");
    }

    [Test]
    public async Task CreateAsync_UnderMemberLimit_CreatesInvitation()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await invitationRepository.Received(1).CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_Success_ReturnsInvitationData()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(10);
        await Assert.That(result.Data!.Email).IsEqualTo("newuser@example.com");
        await Assert.That(result.Data!.AcceptUrl).Contains("token=");
    }

    [Test]
    public async Task CreateAsync_Success_EnqueuesInvitationEmailJob()
    {
        // Intent: after a successful invitation create the handler must enqueue a Hangfire job
        // rather than sending the email inline. This ensures a Resend outage cannot silently
        // drop the email after the invitation is already committed.
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            backgroundJobClient: backgroundJobClient);

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(SendInvitationEmailJob) && j.Method.Name == nameof(SendInvitationEmailJob.SendAsync)),
            Arg.Any<EnqueuedState>());
    }

    // ========== CreateAsync role-forcing tests ==========

    [Test]
    public async Task CreateAsync_ProTier_RequestsViewer_ForcesToTenantAdmin()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(SubscriptionTier.Pro));

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_RequestsViewer_HonorsRequestedRole()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(SubscriptionTier.Team));

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_ProTier_NullRole_DefaultsToTenantAdmin()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(SubscriptionTier.Pro));

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_NullRole_DefaultsToViewer()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        invitationRepository.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(SubscriptionTier.Team));

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    // ========== AcceptAsync tests ==========

    [Test]
    public async Task AcceptAsync_TokenNotFound_Returns404()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("badtoken", Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("badtoken", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task AcceptAsync_AlreadyAccepted_Returns400()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("already been");
    }

    [Test]
    public async Task AcceptAsync_Expired_Returns400()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-8), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("expired");
    }

    [Test]
    public async Task AcceptAsync_EmailMismatch_Returns403()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "invited@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "wrong@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data!.ErrorMessage).Contains("does not match");
    }

    [Test]
    public async Task AcceptAsync_ZeroUserId_Returns401()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task AcceptAsync_AlreadyMember_Returns409()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 5, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task AcceptAsync_NewUser_ReturnsSuccessWithPersonalTenantFlag()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TenantId).IsEqualTo(5);
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsTrue();
    }

    [Test]
    public async Task AcceptAsync_NewUser_CreatesSubscriptionViaSubscriptionRepository()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

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
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        tenantRepository.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        ISubscriptionRepository subscriptionRepository = Substitute.For<ISubscriptionRepository>();
        subscriptionRepository.CreateTenantSubscriptionAsync(Arg.Any<TenantSubscription>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantSubscription s = callInfo.Arg<TenantSubscription>();
            s.Id = 1;

            return s;
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionRepository: subscriptionRepository);

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // The personal tenant role is created unconditionally; the invited tenant role goes through
        // the serializable member-limit guard.
        await tenantRepository.Received(1).CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
        await tenantRepository.Received(1).CreateUserTenantRoleWithMemberLimitAsync(Arg.Any<UserTenantRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_ReturnsSuccessWithoutPersonalTenant()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsFalse();
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_CreatesOneTenantRoleAssignment()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // No personal tenant for an existing user; the invited tenant role goes through the
        // serializable member-limit guard and the plain create is never used.
        await tenantRepository.Received(1).CreateUserTenantRoleWithMemberLimitAsync(Arg.Any<UserTenantRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await tenantRepository.DidNotReceive().CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptAsync_AtMemberLimit_Returns409AndNoRole()
    {
        // Intent: when the serializable guard reports the tenant is at its member limit, acceptance
        // must fail with 409 and the invitation must not be marked accepted.
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        tenantRepository.CreateUserTenantRoleWithMemberLimitAsync(Arg.Any<UserTenantRole>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            subscriptionService: CreateMockSubService(memberLimit: 1));

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("member limit");
        await invitationRepository.DidNotReceive().UpdateInvitationStatusAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<InvitationStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ========== RevokeAsync tests ==========

    [Test]
    public async Task RevokeAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = BuildHandler();

        ServiceResult<ApiResponse<object>> result = await handler.RevokeAsync(1, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task RevokeAsync_InvitationNotFound_Returns404()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<ApiResponse<object>> result = await handler.RevokeAsync(99, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_NotPending_Returns400()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<ApiResponse<object>> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.Message).Contains("pending");
    }

    [Test]
    public async Task RevokeAsync_Success_ReturnsSuccess()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<ApiResponse<object>> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_Success_CallsRevokeOnRepository()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        await handler.RevokeAsync(1, 1, CancellationToken.None);

        await invitationRepository.Received(1).RevokeInvitationAsync(1, 1, Arg.Any<CancellationToken>());
    }

    // ========== ResendAsync tests ==========

    [Test]
    public async Task ResendAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = BuildHandler();

        ServiceResult<InvitationDeliveryResult> result = await handler.ResendAsync(1, null, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task ResendAsync_InvitationNotFound_Returns404()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.ResendAsync(99, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task ResendAsync_NotPending_Returns400()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
        invitationRepository.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Revoked, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task ResendAsync_Success_ReturnsNewInvitationData()
    {
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
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
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(invitationRepository: invitationRepository, tenantRepository: tenantRepository);

        ServiceResult<InvitationDeliveryResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Id).IsEqualTo(20);
        await Assert.That(result.Data!.Email).IsEqualTo("user@test.com");
    }

    [Test]
    public async Task ResendAsync_Success_RevokesOldAndEnqueuesEmailJob()
    {
        // Intent: after revoking the old invitation and committing the new one, the handler must
        // enqueue a Hangfire job rather than sending inline. Verifies revoke + enqueue both happen.
        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        IInvitationRepository invitationRepository = CreateDefaultInvitationRepository();
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
        ITenantRepository tenantRepository = CreateDefaultTenantRepository();
        tenantRepository.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = BuildHandler(
            invitationRepository: invitationRepository,
            tenantRepository: tenantRepository,
            backgroundJobClient: backgroundJobClient);

        await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await invitationRepository.Received(1).RevokeInvitationAsync(1, 1, Arg.Any<CancellationToken>());
        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(SendInvitationEmailJob) && j.Method.Name == nameof(SendInvitationEmailJob.SendAsync)),
            Arg.Any<EnqueuedState>());
    }
}
