// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SocialAuthEvents"/>.
/// </summary>
public sealed class SocialAuthEventsTests
{
    // Registers a real ServerConfigurationService backed by the given settings cache and a mocked Redis
    // (always a cache miss), so the AllowUserSignup read-through resolves and falls through to the cache's
    // GetSettingFromDatabaseAsync — which the signup tests drive.
    private static void AddConfigService(ServiceCollection services)
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        services.AddSingleton(redis);
        services.AddSingleton<ServerConfigurationService>();
    }

    private static (DefaultHttpContext HttpContext, IUserRepository UserRepo, ITenantRepository TenantRepo) CreateTestContext()
    {
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        stampService.GetCurrentStampAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(string.Empty);

        ServiceCollection services = new();
        services.AddSingleton(userRepo);
        services.AddSingleton(tenantRepo);
        services.AddSingleton(settingsCache);
        AddConfigService(services);
        services.AddSingleton(stampService);
        ServiceProvider provider = services.BuildServiceProvider();

        DefaultHttpContext httpContext = new()
        {
            RequestServices = provider
        };

        return (httpContext, userRepo, tenantRepo);
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
        (DefaultHttpContext httpContext, IUserRepository _, ITenantRepository __) = CreateTestContext();
        ClaimsIdentity identity = new(Array.Empty<Claim>(), "TestAuth");

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_EmptyNameIdentifier_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IUserRepository _, ITenantRepository __) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "");

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_FallsBackToSubClaim()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(sub: "sub-123");
        UserAccount user = CreateUser(externalId: "sub-123");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "sub-123", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("sub-123", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsTrue();
        await userRepo.Received(1).GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "sub-123", Arg.Any<CancellationToken>());
    }

    // --- Auto-creation of new users ---

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatesNewUser()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "new-user-ext", email: "new@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "new-user-ext", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("new-user-ext", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsTrue();
        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.CreatedByUserId == 1 &&
            u.IsActive == true &&
            u.IsGlobalAdmin == false &&
            u.IsSystem == false));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsEmailAsUsername()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "new-ext", email: "test@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "new-ext", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("new-ext", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "test@example.com"));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsExternalIdAsUsernameWhenNoEmail()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-no-email");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-no-email", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("ext-no-email", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "ext-no-email"));
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_GetsCorrectExternalId()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "unique-ext-id");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "unique-ext-id", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("unique-ext-id", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.ExternalId == "unique-ext-id"));
    }

    // --- Rejection of invalid users ---

    [Test]
    public async Task PopulateUserClaimsAsync_InactiveUser_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "inactive-ext");
        UserAccount user = CreateUser(externalId: "inactive-ext", isActive: false);

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "inactive-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_SystemUser_ReturnsFalse()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "system-ext");
        UserAccount user = CreateUser(externalId: "system-ext", isSystem: true);

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "system-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task PopulateUserClaimsAsync_InactiveUser_RemovesNameIdentifierClaim()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "inactive-ext");
        UserAccount user = CreateUser(externalId: "inactive-ext", isActive: false);

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "inactive-ext", Arg.Any<CancellationToken>())
            .Returns(user);

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? nameIdClaim = identity.FindFirst(ClaimTypes.NameIdentifier);

        await Assert.That(nameIdClaim).IsNull();
    }

    // --- Email update logic ---

    [Test]
    public async Task PopulateUserClaimsAsync_UpdatesUsernameWhenEmailDiffers()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1", email: "new@example.com");
        UserAccount user = CreateUser(id: 42, externalId: "ext-1", username: "old@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.Received(1).UpdateUserEmailAsync(42, "new@example.com", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_DoesNotUpdateWhenEmailMatchesCaseInsensitive()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1", email: "user@x.com");
        UserAccount user = CreateUser(externalId: "ext-1", username: "User@X.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.DidNotReceive().UpdateUserEmailAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_DoesNotUpdateWhenEmailClaimIsEmpty()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1", username: "user@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.DidNotReceive().UpdateUserEmailAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Claims population ---

    [Test]
    public async Task PopulateUserClaimsAsync_AddsTenantRoleClaims()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(id: 5, externalId: "ext-1");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
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
        tenantRepo.GetTenantsForUserByIdAsync(5, Arg.Any<CancellationToken>())
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
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(id: 42, externalId: "ext-1");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? actorClaim = identity.FindFirst(ClaimTypes.Actor);

        await Assert.That(actorClaim).IsNotNull();
        await Assert.That(actorClaim!.Value).IsEqualTo("42");
    }

    [Test]
    public async Task PopulateUserClaimsAsync_AddsIgaClaimWithGlobalAdminFlag()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1", isGlobalAdmin: true);

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? igaClaim = identity.FindFirst("iga");

        await Assert.That(igaClaim).IsNotNull();
        await Assert.That(igaClaim!.Value).IsEqualTo("True");
    }

    [Test]
    public async Task PopulateUserClaimsAsync_UserWithNoTenantRoles_GetsZeroRoleClaims()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-1");
        UserAccount user = CreateUser(externalId: "ext-1");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-1", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-1", Arg.Any<CancellationToken>())
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

    // --- Provider-scoped identity resolution ---

    [Test]
    public async Task PopulateUserClaimsAsync_ResolvesIdentityScopedByProvider()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "shared-sub");
        UserAccount googleUser = CreateUser(id: 7, externalId: "shared-sub");
        googleUser.AuthProvider = Database.Enums.AuthProviderType.Google;

        userRepo.GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.Google, "shared-sub", Arg.Any<CancellationToken>())
            .Returns(googleUser);
        tenantRepo.GetTenantsForUserAsync("shared-sub", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Google);

        await Assert.That(result).IsTrue();
        await userRepo.Received(1).GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.Google, "shared-sub", Arg.Any<CancellationToken>());
        await userRepo.DidNotReceive().GetUserByExternalIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Auth provider update tests ---

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_UpdatesAuthProvider_WhenDifferent()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-provider-update");
        UserAccount user = CreateUser(id: 50, externalId: "ext-provider-update");
        user.AuthProvider = Database.Enums.AuthProviderType.GitHub;

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-provider-update", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-provider-update", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Google);

        await userRepo.Received(1).UpdateUserAuthProviderAsync(50, Database.Enums.AuthProviderType.Google, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_SkipsAuthProviderUpdate_WhenSame()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-same-provider");
        UserAccount user = CreateUser(id: 51, externalId: "ext-same-provider");
        user.AuthProvider = Database.Enums.AuthProviderType.Google;

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-same-provider", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-same-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Google);

        await userRepo.DidNotReceive().UpdateUserAuthProviderAsync(Arg.Any<int>(), Arg.Any<Database.Enums.AuthProviderType>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PopulateUserClaimsAsync_ExistingUser_SkipsAuthProviderUpdate_WhenUnknown()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-unknown-provider");
        UserAccount user = CreateUser(id: 52, externalId: "ext-unknown-provider");
        user.AuthProvider = Database.Enums.AuthProviderType.GitHub;

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-unknown-provider", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-unknown-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Unknown);

        await userRepo.DidNotReceive().UpdateUserAuthProviderAsync(Arg.Any<int>(), Arg.Any<Database.Enums.AuthProviderType>(), Arg.Any<CancellationToken>());
    }

    // --- Email claim fallback ---

    [Test]
    public async Task PopulateUserClaimsAsync_UsesEmailClaimType_WhenClaimTypesEmailMissing()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();

        // Create identity with "email" claim instead of ClaimTypes.Email
        List<Claim> claims = new()
        {
            new Claim(ClaimTypes.NameIdentifier, "ext-fallback-email"),
            new Claim("email", "fallback@example.com"),
        };
        ClaimsIdentity identity = new(claims, "TestAuth");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-fallback-email", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("ext-fallback-email", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.Username == "fallback@example.com"));
    }

    // --- Auto-created user sets auth provider ---

    [Test]
    public async Task PopulateUserClaimsAsync_AutoCreatedUser_SetsAuthProvider()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-new-with-provider", email: "new-prov@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-new-with-provider", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);
        userRepo.CreateUserAccountAsync(Arg.Any<UserAccount>())
            .Returns(callInfo => callInfo.Arg<UserAccount>());
        tenantRepo.GetTenantsForUserAsync("ext-new-with-provider", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.Microsoft);

        await userRepo.Received(1).CreateUserAccountAsync(Arg.Is<UserAccount>(u =>
            u.AuthProvider == Database.Enums.AuthProviderType.Microsoft));
    }

    // --- Signup disabled blocks new user ---

    [Test]
    public async Task PopulateUserClaimsAsync_SignupDisabled_BlocksNewUser()
    {
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        settingsCache.GetSettingFromDatabaseAsync(Database.Enums.ServerConfigurationSettingKeys.AllowUserSignup, Arg.Any<CancellationToken>())
            .Returns("false");

        ServiceCollection services = new();
        services.AddSingleton(userRepo);
        services.AddSingleton(tenantRepo);
        services.AddSingleton(settingsCache);
        AddConfigService(services);
        ServiceProvider provider = services.BuildServiceProvider();

        DefaultHttpContext httpContext = new()
        {
            RequestServices = provider
        };

        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-new-blocked", email: "blocked@example.com");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-new-blocked", Arg.Any<CancellationToken>())
            .Returns((UserAccount?)null);

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await Assert.That(result).IsFalse();
        await userRepo.DidNotReceive().CreateUserAccountAsync(Arg.Any<UserAccount>());
    }

    // --- Global admin false sets iga to False ---

    [Test]
    public async Task PopulateUserClaimsAsync_GlobalAdminFalse_SetsIgaFalse()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-non-admin");
        UserAccount user = CreateUser(externalId: "ext-non-admin", isGlobalAdmin: false);

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-non-admin", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserAsync("ext-non-admin", Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        Claim? igaClaim = identity.FindFirst("iga");

        await Assert.That(igaClaim).IsNotNull();
        await Assert.That(igaClaim!.Value).IsEqualTo("False");
    }

    // --- Tenant role resolution is scoped to the resolved user id ---

    [Test]
    public async Task PopulateUserClaimsAsync_ResolvesTenantRolesByUserId_NotExternalId()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "shared-sub");
        UserAccount user = CreateUser(id: 99, externalId: "shared-sub");

        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "shared-sub", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None);

        await tenantRepo.Received(1).GetTenantsForUserByIdAsync(99, Arg.Any<CancellationToken>());
        await tenantRepo.DidNotReceive().GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Auth provider claim minting ---

    [Test]
    public async Task PopulateUserClaimsAsync_AddsAuthProviderClaim()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-apr");
        UserAccount user = CreateUser(id: 77, externalId: "ext-apr");
        user.AuthProvider = Database.Enums.AuthProviderType.GitHub;

        userRepo.GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.GitHub, "ext-apr", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserByIdAsync(77, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.GitHub);

        Claim? aprClaim = identity.FindFirst("apr");

        await Assert.That(aprClaim).IsNotNull();
        await Assert.That(aprClaim!.Value).IsEqualTo(((short)Database.Enums.AuthProviderType.GitHub).ToString());
    }

    // --- Security stamp claim minting ---

    [Test]
    public async Task PopulateUserClaimsAsync_MintsSecurityStampClaim()
    {
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        stampService.GetCurrentStampAsync(9, Arg.Any<CancellationToken>()).Returns("stamp-9");

        ServiceCollection services = new();
        services.AddSingleton(userRepo);
        services.AddSingleton(tenantRepo);
        services.AddSingleton(settingsCache);
        AddConfigService(services);
        services.AddSingleton(stampService);
        DefaultHttpContext httpContext = new() { RequestServices = services.BuildServiceProvider() };

        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "ext-stamp");
        UserAccount user = CreateUser(id: 9, externalId: "ext-stamp");
        userRepo.GetUserByExternalIdForProviderAsync(Arg.Any<Database.Enums.AuthProviderType>(), "ext-stamp", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserByIdAsync(9, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.GitHub);

        Claim? sstClaim = identity.FindFirst("sst");

        await Assert.That(sstClaim).IsNotNull();
        await Assert.That(sstClaim!.Value).IsEqualTo("stamp-9");
    }

    // --- BuildTenantNamespacedSubject ---

    [Test]
    public async Task BuildTenantNamespacedSubject_WithTenantId_PrefixesSubject()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Items["tenant-oidc-tenant-id"] = "42";

        string result = SocialAuthEvents.BuildTenantNamespacedSubject(httpContext, "raw-sub");

        await Assert.That(result).IsEqualTo("tenant:42:raw-sub");
    }

    [Test]
    public async Task BuildTenantNamespacedSubject_MissingTenantId_Throws()
    {
        DefaultHttpContext httpContext = new();

        await Assert.That(() => SocialAuthEvents.BuildTenantNamespacedSubject(httpContext, "raw-sub"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuildTenantNamespacedSubject_EmptyTenantId_Throws()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Items["tenant-oidc-tenant-id"] = "";

        await Assert.That(() => SocialAuthEvents.BuildTenantNamespacedSubject(httpContext, "raw-sub"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuildTenantNamespacedSubject_AlreadyNamespacedSubject_DoesNotDoubleWrap()
    {
        DefaultHttpContext httpContext = new();
        httpContext.Items["tenant-oidc-tenant-id"] = "5";

        string result = SocialAuthEvents.BuildTenantNamespacedSubject(httpContext, "tenant:5:rawsub");

        await Assert.That(result).IsEqualTo("tenant:5:rawsub");
    }

    // --- Custom OIDC rewrites the cookie subject to the namespaced value ---

    [Test]
    public async Task PopulateUserClaimsAsync_CustomOidc_RewritesNameIdentifierToNamespacedSubject()
    {
        (DefaultHttpContext httpContext, IUserRepository userRepo, ITenantRepository tenantRepo) = CreateTestContext();
        httpContext.Items["tenant-oidc-tenant-id"] = "5";

        ClaimsIdentity identity = CreateIdentity(nameIdentifier: "rawsub");
        UserAccount user = CreateUser(id: 88, externalId: "tenant:5:rawsub");
        user.AuthProvider = Database.Enums.AuthProviderType.CustomOidc;

        userRepo.GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.CustomOidc, "tenant:5:rawsub", Arg.Any<CancellationToken>())
            .Returns(user);
        tenantRepo.GetTenantsForUserByIdAsync(88, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<UserTenantRole>());

        bool result = await SocialAuthEvents.PopulateUserClaimsAsync(identity, httpContext, CancellationToken.None, Database.Enums.AuthProviderType.CustomOidc);

        await Assert.That(result).IsTrue();

        // The cookie subject must now match the namespaced value the DB keys on, so later reads resolve.
        Claim? nameIdClaim = identity.FindFirst(ClaimTypes.NameIdentifier);
        await Assert.That(nameIdClaim).IsNotNull();
        await Assert.That(nameIdClaim!.Value).IsEqualTo("tenant:5:rawsub");

        // The user lookup must have been performed with the namespaced subject, not the raw one.
        await userRepo.Received(1).GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.CustomOidc, "tenant:5:rawsub", Arg.Any<CancellationToken>());
        await userRepo.DidNotReceive().GetUserByExternalIdForProviderAsync(Database.Enums.AuthProviderType.CustomOidc, "rawsub", Arg.Any<CancellationToken>());
    }
}
