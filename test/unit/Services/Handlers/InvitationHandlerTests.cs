// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Notifications;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="InvitationHandler"/>.
/// </summary>
public class InvitationHandlerTests
{
    private static IDatabaseCache CreateMockCache()
    {
        return Substitute.For<IDatabaseCache>();
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
            Id = 1, TenantId = 1, Tier = tier, Status = SubscriptionStatus.Active, MachineLimit = null,
            RetentionDays = 30, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        return svc;
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_InvalidEmail_Returns400()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("notanemail", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("valid email");
    }

    [Test]
    public async Task CreateAsync_EmptyEmail_Returns400()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, null, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task CreateAsync_FreeTierSubscription_Returns402()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Free));

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(402);
        await Assert.That(result.Data!.ErrorMessage).Contains("Upgrade");
    }

    [Test]
    public async Task CreateAsync_ExistingPendingInvitation_Returns409()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>())
            .Returns(new TenantInvitation { Id = 1, Email = "user@example.com", TenantId = 1, TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("pending invitation already exists");
    }

    [Test]
    public async Task CreateAsync_AlreadyMember_Returns409()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("user@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        UserAccount memberUser = new() { Id = 5, ExternalId = "ext-5", Username = "user@example.com", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 0, IsActive = true, IsSystem = false, IsGlobalAdmin = false };
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 5, User = memberUser, AssignedTenantId = 1, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("user@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task CreateAsync_Success_ReturnsInvitationData()
    {
        IDatabaseCache cache = CreateMockCache();
        IEmailService emailService = CreateMockEmailService();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, emailService, CreateMockSubService());

        ServiceResult<InvitationCreateResult> result = await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Id).IsEqualTo(10);
        await Assert.That(result.Data!.Email).IsEqualTo("newuser@example.com");
        await Assert.That(result.Data!.AcceptUrl).Contains("token=");
    }

    [Test]
    public async Task CreateAsync_Success_SendsInvitationEmail()
    {
        IDatabaseCache cache = CreateMockCache();
        IEmailService emailService = CreateMockEmailService();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, emailService, CreateMockSubService());

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await emailService.Received(1).SendInvitationEmailAsync("newuser@example.com", "Test Org", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== CreateAsync role-forcing tests ==========

    [Test]
    public async Task CreateAsync_ProTier_RequestsViewer_ForcesToTenantAdmin()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Pro));

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await cache.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_RequestsViewer_HonorsRequestedRole()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Team));

        await handler.CreateAsync("newuser@example.com", "Viewer", 1, 1, "https://app.test", CancellationToken.None);

        await cache.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_ProTier_NullRole_DefaultsToTenantAdmin()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Pro));

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await cache.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.TenantAdmin),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TeamTier_NullRole_DefaultsToViewer()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetPendingInvitationByEmailAndTenantAsync("newuser@example.com", 1, Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        cache.GetMembersForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 10;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService(SubscriptionTier.Team));

        await handler.CreateAsync("newuser@example.com", null, 1, 1, "https://app.test", CancellationToken.None);

        await cache.Received(1).CreateInvitationAsync(
            Arg.Is<TenantInvitation>(i => i.Role == UserAccountRoles.Viewer),
            Arg.Any<CancellationToken>());
    }

    // ========== AcceptAsync tests ==========

    [Test]
    public async Task AcceptAsync_TokenNotFound_Returns404()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("badtoken", Arg.Any<CancellationToken>()).Returns((TenantInvitation?)null);
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("badtoken", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task AcceptAsync_AlreadyAccepted_Returns400()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("already been");
    }

    [Test]
    public async Task AcceptAsync_Expired_Returns400()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-8), ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("expired");
    }

    [Test]
    public async Task AcceptAsync_EmailMismatch_Returns403()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "invited@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "wrong@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
        await Assert.That(result.Data!.ErrorMessage).Contains("does not match");
    }

    [Test]
    public async Task AcceptAsync_ZeroUserId_Returns401()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 0, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task AcceptAsync_AlreadyMember_Returns409()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 5, Role = UserAccountRoles.Viewer, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(409);
        await Assert.That(result.Data!.ErrorMessage).Contains("already a member");
    }

    [Test]
    public async Task AcceptAsync_NewUser_ReturnsSuccessWithPersonalTenantFlag()
    {
        IDatabaseCache cache = CreateMockCache();
        ISubscriptionService subService = CreateMockSubService();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        subService.ProvisionFreeSubscriptionAsync(99, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 99, Tier = SubscriptionTier.Free, Status = SubscriptionStatus.Active,
            RetentionDays = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), subService);

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.TenantId).IsEqualTo(5);
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsEqualTo(true);
    }

    [Test]
    public async Task AcceptAsync_NewUser_CreatesTwoTenantRoleAssignments()
    {
        IDatabaseCache cache = CreateMockCache();
        ISubscriptionService subService = CreateMockSubService();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<UserTenantRole>());
        cache.CreateTenantAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            Tenant t = callInfo.Arg<Tenant>();
            t.Id = 99;

            return t;
        });
        subService.ProvisionFreeSubscriptionAsync(99, Arg.Any<CancellationToken>()).Returns(new TenantSubscription
        {
            Id = 1, TenantId = 99, Tier = SubscriptionTier.Free, Status = SubscriptionStatus.Active,
            RetentionDays = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), subService);

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // Should have created personal tenant role + invitation tenant role = 2 calls
        await cache.Received(2).CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_ReturnsSuccessWithoutPersonalTenant()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationAcceptResult> result = await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.PersonalTenantProvisioned).IsEqualTo(false);
    }

    [Test]
    public async Task AcceptAsync_ExistingUser_CreatesOneTenantRoleAssignment()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationByTokenAsync("token", Arg.Any<CancellationToken>()).Returns(new TenantInvitation
        {
            Id = 1, TenantId = 5, Email = "user@test.com", TokenHash = "token", Role = UserAccountRoles.TenantAdmin, Status = InvitationStatus.Pending,
            InvitedByUserId = 2, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        });
        cache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>()).Returns(new List<UserTenantRole>
        {
            new() { UserId = 1, AssignedTenantId = 10, Role = UserAccountRoles.TenantAdmin, AssignedByUserId = 1, AssignedAt = DateTimeOffset.UtcNow, IsActive = true }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        await handler.AcceptAsync("token", "user@test.com", 1, "ext-1", CancellationToken.None);

        // Only 1 call for the invitation tenant role (no personal tenant)
        await cache.Received(1).CreateUserTenantRoleAsync(Arg.Any<UserTenantRole>(), Arg.Any<CancellationToken>());
    }

    // ========== RevokeAsync tests ==========

    [Test]
    public async Task RevokeAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, null, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task RevokeAsync_InvitationNotFound_Returns404()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(99, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task RevokeAsync_NotPending_Returns400()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Accepted, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.Data!.ErrorMessage).Contains("pending");
    }

    [Test]
    public async Task RevokeAsync_Success_ReturnsSuccess()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationRevokeResult> result = await handler.RevokeAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    [Test]
    public async Task RevokeAsync_Success_CallsRevokeOnCache()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        await handler.RevokeAsync(1, 1, CancellationToken.None);

        await cache.Received(1).RevokeInvitationAsync(1, Arg.Any<CancellationToken>());
    }

    // ========== ResendAsync tests ==========

    [Test]
    public async Task ResendAsync_NullTenantId_Returns401()
    {
        InvitationHandler handler = new(CreateMockCache(), CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, null, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task ResendAsync_InvitationNotFound_Returns404()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(Enumerable.Empty<TenantInvitation>());
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(99, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsNotFound).IsEqualTo(true);
    }

    [Test]
    public async Task ResendAsync_NotPending_Returns400()
    {
        IDatabaseCache cache = CreateMockCache();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "abc", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Revoked, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        InvitationHandler handler = new(cache, CreateMockEmailService(), CreateMockSubService());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task ResendAsync_Success_ReturnsNewInvitationData()
    {
        IDatabaseCache cache = CreateMockCache();
        IEmailService emailService = CreateMockEmailService();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "oldtoken", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 20;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, emailService, CreateMockSubService());

        ServiceResult<InvitationResendResult> result = await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Id).IsEqualTo(20);
        await Assert.That(result.Data!.Email).IsEqualTo("user@test.com");
    }

    [Test]
    public async Task ResendAsync_Success_RevokesOldAndSendsEmail()
    {
        IDatabaseCache cache = CreateMockCache();
        IEmailService emailService = CreateMockEmailService();
        cache.GetInvitationsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new List<TenantInvitation>
        {
            new() { Id = 1, TenantId = 1, Email = "user@test.com", TokenHash = "oldtoken", Role = UserAccountRoles.Viewer, Status = InvitationStatus.Pending, InvitedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) }
        });
        cache.CreateInvitationAsync(Arg.Any<TenantInvitation>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            TenantInvitation inv = callInfo.Arg<TenantInvitation>();
            inv.Id = 20;

            return inv;
        });
        cache.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            Id = 1, Name = "Test Org", ExternalId = "ext-1", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = 1, IsActive = true, LogoUrl = ""
        });
        InvitationHandler handler = new(cache, emailService, CreateMockSubService());

        await handler.ResendAsync(1, 1, 1, "inviter@test.com", "https://app.test", CancellationToken.None);

        await cache.Received(1).RevokeInvitationAsync(1, Arg.Any<CancellationToken>());
        await emailService.Received(1).SendInvitationEmailAsync("user@test.com", "Test Org", "inviter@test.com", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
