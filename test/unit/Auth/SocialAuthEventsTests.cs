// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SocialAuthEvents"/>.
/// </summary>
public sealed class SocialAuthEventsTests
{
    private static (DefaultHttpContext HttpContext, IDatabaseCache DbCache) CreateTestContext()
    {
        IDatabaseCache dbCache = Substitute.For<IDatabaseCache>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();

        ServiceCollection services = new();
        services.AddSingleton(dbCache);
        services.AddSingleton(settingsCache);
        ServiceProvider provider = services.BuildServiceProvider();

        DefaultHttpContext httpContext = new()
        {
            RequestServices = provider
        };

        return (httpContext, dbCache);
    }

    private static ClaimsIdentity CreateIdentity(string? nameIdentifier = null, string? sub = null, string? email = null)
    {
        List<Claim> claims = new();

        if (nameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }

        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        if (email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        return new ClaimsIdentity(claims, "TestAuth");
    }

    private static UserAccount CreateUser(
        int id = 1,
        string externalId = "ext-1",
        string username = "user@example.com",
        bool isActive = true,
        bool isSystem = false,
        bool isGlobalAdmin = false)
    {
        return new UserAccount
        {
            Id = id,
            ExternalId = externalId,
            Username = username,
            IsActive = isActive,
            IsSystem = isSystem,
            IsGlobalAdmin = isGlobalAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1
        };
    }

    // --- Missing/empty identity claims ---

    [Test]
    public async Task PopulateUserClaimsAsync_NoNameIdentifierOrSubClaim_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IDatabaseCache _) = CreateTestContext();
        ClaimsIdentity identity = new(Array.Empty<Claim>(), "TestAuth");

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_EmptyNameIdentifier_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IDatabaseCache _) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "");

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_FallsBackToSubClaim()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(sub: "sub-123");
        UserAccount user = CreateUser(externalId: "sub-123");

        dbCache.GetUserByExternalIdAsync("sub-123", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("sub-123", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsTrue();
        await dbCache.Received(1).GetUserByExternalIdAsync("sub-123", Arg.Any<CancellationToken>());
    }

    // --- Auto-creation of new users ---

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatesNewUser()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "new-user-ext", email: "new@example.com");

        dbCache.GetUserByExternalIdAsync("new-user-ext", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("new-user-ext", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsTrue();
        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.CreatedByUserId == 1 &&
            u.IsActive == true &&
            u.IsGlobalAdmin == false &&
            u.IsSystem == false));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsEmailAsUsername()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "new-ext", email: "test@example.com");

        dbCache.GetUserByExternalIdAsync("new-ext", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("new-ext", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "test@example.com"));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsExternalIdAsUsernameWhenNoEmail()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-no-email");

        dbCache.GetUserByExternalIdAsync("ext-no-email", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("ext-no-email", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "ext-no-email"));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsCorrectExternalId()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "unique-ext-id");

        dbCache.GetUserByExternalIdAsync("unique-ext-id", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("unique-ext-id", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.ExternalId == "unique-ext-id"));
    }

    // --- Rejection of invalid users ---

    [Test]
    public async Task PopulateUserClaimsAsync_InactiveUser_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "inactive-ext");
        UserAccount user = CreateUser(externalId: "inactive-ext", isActive: false);

        dbCache.GetUserByExternalIdAsync("inactive-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_SystemUser_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "system-ext");
        UserAccount user = CreateUser(externalId: "system-ext", isSystem: true);

        dbCache.GetUserByExternalIdAsync("system-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_InactiveUser_RemovesNameIdentifierClaim()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "inactive-ext");
        UserAccount user = CreateUser(externalId: "inactive-ext", isActive: false);

        dbCache.GetUserByExternalIdAsync("inactive-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? nameIdClaim = identity.FindFirst(ClaimTypes.NameIdentifier);

        await Assert.That(nameIdClaim).IsNull();
    }

    // --- Email update logic ---

    [Test]
    public async Task PopulateUserClaimsAsync_UpdatesUsernameWhenEmailDiffers()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1", email: "new@example.com");
        UserAccount user = CreateUser(id: 42, externalId: "ext-1", username: "old@example.com");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.Received(1).UpdateUserEmailAsync(42, "new@example.com", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_DoesNotUpdateWhenEmailMatchesCaseInsensitive()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1", email: "user@x.com");
        UserAccount user = CreateUser(externalId: "ext-1", username: "User@X.com");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.DidNotReceive().UpdateUserEmailAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_DoesNotUpdateWhenEmailClaimIsEmpty()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1", username: "user@example.com");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.DidNotReceive().UpdateUserEmailAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Claims population ---

    [Test]
    public async Task PopulateUserClaimsAsync_AddsTenantRoleClaims()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(id: 5, externalId: "ext-1");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);

        List<UserTenantRole> roles = new()
        {
            new UserTenantRole
            {
                UserId = 5,
                AssignedTenantId = 10,
                Role = Database.Enums.UserAccountRoles.TenantAdmin,
                AssignedByUserId = 1,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true
            },
            new UserTenantRole
            {
                UserId = 5,
                AssignedTenantId = 20,
                Role = Database.Enums.UserAccountRoles.Viewer,
                AssignedByUserId = 1,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true
            }
        };
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(roles);

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        List<Claim> roleClaims = identity.FindAll(ClaimTypes.Role).ToList();

        await Assert.That(roleClaims.Count).IsEqualTo(2);
        await Assert.That(roleClaims.Any(c => c.Value == $"10:{(byte)Database.Enums.UserAccountRoles.TenantAdmin}")).IsTrue();
        await Assert.That(roleClaims.Any(c => c.Value == $"20:{(byte)Database.Enums.UserAccountRoles.Viewer}")).IsTrue();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AddsActorClaimWithUserId()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(id: 42, externalId: "ext-1");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? actorClaim = identity.FindFirst(ClaimTypes.Actor);

        await Assert.That(actorClaim).IsNotNull();
        await Assert.That(actorClaim!.Value).IsEqualTo("42");
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AddsIgaClaimWithGlobalAdminFlag()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1", isGlobalAdmin: true);

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? igaClaim = identity.FindFirst("iga");

        await Assert.That(igaClaim).IsNotNull();
        await Assert.That(igaClaim!.Value).IsEqualTo("True");
    }

    [Test]
    public async Task PopulateUserClaimsAsync_UserWithNoTenantRoles_GetsZeroRoleClaims()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1");

        dbCache.GetUserByExternalIdAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        List<Claim> roleClaims = identity.FindAll(ClaimTypes.Role).ToList();

        await Assert.That(roleClaims.Count).IsEqualTo(0);
    }

    // --- ResolveProviderFromScheme Tests ---

    [Test]
    public async Task ResolveProviderFromScheme_GitHub_ReturnsGitHub()
    {
        Database.Enums.AuthProviderType result = SocialAuthEvents.ResolveProviderFromScheme("github");

        await Assert.That(result).IsEqualTo(Database.Enums.AuthProviderType.GitHub);
    }

    [Test]
    public async Task ResolveProviderFromScheme_Google_ReturnsGoogle()
    {
        Database.Enums.AuthProviderType result = SocialAuthEvents.ResolveProviderFromScheme("google");

        await Assert.That(result).IsEqualTo(Database.Enums.AuthProviderType.Google);
    }

    [Test]
    public async Task ResolveProviderFromScheme_Microsoft_ReturnsMicrosoft()
    {
        Database.Enums.AuthProviderType result = SocialAuthEvents.ResolveProviderFromScheme("microsoft");

        await Assert.That(result).IsEqualTo(Database.Enums.AuthProviderType.Microsoft);
    }

    [Test]
    public async Task ResolveProviderFromScheme_UnknownScheme_ReturnsUnknown()
    {
        Database.Enums.AuthProviderType result = SocialAuthEvents.ResolveProviderFromScheme("some-random-provider");

        await Assert.That(result).IsEqualTo(Database.Enums.AuthProviderType.Unknown);
    }

    [Test]
    public async Task ResolveProviderFromScheme_Null_ReturnsUnknown()
    {
        Database.Enums.AuthProviderType result = SocialAuthEvents.ResolveProviderFromScheme(null!);

        await Assert.That(result).IsEqualTo(Database.Enums.AuthProviderType.Unknown);
    }

    // --- Auth provider update tests ---

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_UpdatesAuthProvider_WhenDifferent()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-provider-update");
        UserAccount user = CreateUser(id: 50, externalId: "ext-provider-update");
        user.AuthProvider = Database.Enums.AuthProviderType.GitHub;

        dbCache.GetUserByExternalIdAsync("ext-provider-update", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-provider-update", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Google);

        await dbCache.Received(1).UpdateUserAuthProviderAsync(50, Database.Enums.AuthProviderType.Google, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_SkipsAuthProviderUpdate_WhenSame()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-same-provider");
        UserAccount user = CreateUser(id: 51, externalId: "ext-same-provider");
        user.AuthProvider = Database.Enums.AuthProviderType.Google;

        dbCache.GetUserByExternalIdAsync("ext-same-provider", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-same-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Google);

        await dbCache.DidNotReceive().UpdateUserAuthProviderAsync(Arg.Any<int>(), Arg.Any<Database.Enums.AuthProviderType>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_SkipsAuthProviderUpdate_WhenUnknown()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-unknown-provider");
        UserAccount user = CreateUser(id: 52, externalId: "ext-unknown-provider");
        user.AuthProvider = Database.Enums.AuthProviderType.GitHub;

        dbCache.GetUserByExternalIdAsync("ext-unknown-provider", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-unknown-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Unknown);

        await dbCache.DidNotReceive().UpdateUserAuthProviderAsync(Arg.Any<int>(), Arg.Any<Database.Enums.AuthProviderType>(), Arg.Any<CancellationToken>());
    }

    // --- Email claim fallback ---

    [Test]
    public async Task PopulateUserClaimsAsync_UsesEmailClaimType_WhenClaimTypesEmailMissing()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();

        // Create identity with "email" claim instead of ClaimTypes.Email
        List<Claim> claims = new()
        {
            new Claim(ClaimTypes.NameIdentifier, "ext-fallback-email"),
            new Claim("email", "fallback@example.com"),
        };
        ClaimsIdentity identity = new(claims, "TestAuth");

        dbCache.GetUserByExternalIdAsync("ext-fallback-email", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("ext-fallback-email", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "fallback@example.com"));
    }

    // --- Auto-created user sets auth provider ---

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_SetsAuthProvider()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-new-with-provider", email: "new-prov@example.com");

        dbCache.GetUserByExternalIdAsync("ext-new-with-provider", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        dbCache.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        dbCache.GetTenantsForUserAsync("ext-new-with-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Microsoft);

        await dbCache.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.AuthProvider == Database.Enums.AuthProviderType.Microsoft));
    }

    // --- Global admin false sets iga to False ---

    [Test]
    public async Task PopulateUserClaimsAsync_GlobalAdminFalse_SetsIgaFalse()
    {
        (DefaultHttpContext httpContext, IDatabaseCache dbCache) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-non-admin");
        UserAccount user = CreateUser(externalId: "ext-non-admin", isGlobalAdmin: false);

        dbCache.GetUserByExternalIdAsync("ext-non-admin", Arg.Any<CancellationToken>())
            .Returns(user);
        dbCache.GetTenantsForUserAsync("ext-non-admin", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? igaClaim = identity.FindFirst("iga");

        await Assert.That(igaClaim).IsNotNull();
        await Assert.That(igaClaim!.Value).IsEqualTo("False");
    }
}
