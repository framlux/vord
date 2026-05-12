// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="CookiePrincipalValidator"/>.
/// Verifies that inactive users are rejected, and that stale role claims
/// in the cookie are detected and refreshed from the database.
/// </summary>
public class CookiePrincipalValidatorTests
{
    private static CookiePrincipalValidator CreateValidator(
        IDatabase redisDb,
        ITenantRepository tenantRepository,
        IServiceScopeFactory scopeFactory)
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        ILogger<CookiePrincipalValidator> logger = new NullLogger<CookiePrincipalValidator>();

        return new CookiePrincipalValidator(redis, tenantRepository, scopeFactory, logger);
    }

    private static IDatabase CreateRedisDb(Dictionary<string, string>? entries = null)
    {
        IDatabase redisDb = Substitute.For<IDatabase>();

        // Default: cache miss (returns RedisValue.Null)
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        if (entries is not null)
        {
            foreach (KeyValuePair<string, string> entry in entries)
            {
                redisDb.StringGetAsync((RedisKey)entry.Key, Arg.Any<CommandFlags>())
                    .Returns((RedisValue)entry.Value);
            }
        }

        return redisDb;
    }

    // --- Active user check ---

    [Test]
    public async Task ActiveUser_CachedAsActive_IsAllowed()
    {
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "1"
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        bool result = await validator.CheckUserIsActiveAsync(1, redisDb, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task InactiveUser_CachedAsInactive_IsRejected()
    {
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "0"
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        bool result = await validator.CheckUserIsActiveAsync(1, redisDb, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ActiveUser_CacheMiss_QueriesDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser(isActive: true);
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        bool result = await validator.CheckUserIsActiveAsync(user.Id, redisDb, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task InactiveUser_CacheMiss_QueriesDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user = TestDataBuilder.BuildUser(isActive: false);
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        bool result = await validator.CheckUserIsActiveAsync(user.Id, redisDb, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task NonexistentUser_CacheMiss_IsRejected()
    {
        using TestDatabaseFactory dbFactory = new();

        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        bool result = await validator.CheckUserIsActiveAsync(99999, redisDb, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    // --- Role claim refresh ---

    [Test]
    public async Task RoleClaims_WhenMatchingDatabase_AreNotRefreshed()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>
            {
                TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: 10, role: UserAccountRoles.TenantAdmin)
            });

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Cookie has the same role: "10:1" (TenantAdmin = 1)
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);

        await validator.RefreshRoleClaimsIfChangedAsync(context, 1, "ext-123", redisDb);

        // ShouldRenew stays false because claims match
        await Assert.That(context.ShouldRenew).IsFalse();
    }

    [Test]
    public async Task RoleClaims_WhenDifferentFromDatabase_AreRefreshed()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        // Database has two roles now (user was added to a second tenant)
        tenantRepo.GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>
            {
                TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: 10, role: UserAccountRoles.TenantAdmin),
                TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: 20, role: UserAccountRoles.Viewer),
            });

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Cookie only has the original role
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);

        await validator.RefreshRoleClaimsIfChangedAsync(context, 1, "ext-123", redisDb);

        // Cookie should be renewed with updated claims
        await Assert.That(context.ShouldRenew).IsTrue();

        // Principal should have both roles now
        List<string> updatedRoles = context.Principal!.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .OrderBy(v => v)
            .ToList();
        await Assert.That(updatedRoles.Count).IsEqualTo(2);
        await Assert.That(updatedRoles[0]).IsEqualTo($"10:{(byte)UserAccountRoles.TenantAdmin}");
        await Assert.That(updatedRoles[1]).IsEqualTo($"20:{(byte)UserAccountRoles.Viewer}");
    }

    [Test]
    public async Task RoleClaims_WhenRoleRevoked_AreRemoved()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        // Database has no roles for this user (all revoked)
        tenantRepo.GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Cookie still has the old role
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);

        await validator.RefreshRoleClaimsIfChangedAsync(context, 1, "ext-123", redisDb);

        await Assert.That(context.ShouldRenew).IsTrue();

        List<string> updatedRoles = context.Principal!.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToList();
        await Assert.That(updatedRoles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RoleClaims_WhenCachedInRedis_DoesNotQueryDatabase()
    {
        // Redis has the current roles cached
        string cachedRoles = $"10:{(byte)UserAccountRoles.TenantAdmin}";
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:roles:1"] = cachedRoles
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Cookie matches the cached value
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);

        await validator.RefreshRoleClaimsIfChangedAsync(context, 1, "ext-123", redisDb);

        // No database call should have been made
        await tenantRepo.DidNotReceive().GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Assert.That(context.ShouldRenew).IsFalse();
    }

    // --- Redirect overrides ---

    [Test]
    public async Task RedirectToLogin_Returns401()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        DefaultHttpContext httpContext = new();
        RedirectContext<CookieAuthenticationOptions> redirectContext = new(
            httpContext,
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationProperties(),
            "/auth/login");

        await validator.RedirectToLogin(redirectContext);

        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(401);
    }

    [Test]
    public async Task RedirectToAccessDenied_Returns403()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        DefaultHttpContext httpContext = new();
        RedirectContext<CookieAuthenticationOptions> redirectContext = new(
            httpContext,
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationProperties(),
            "/auth/access-denied");

        await validator.RedirectToAccessDenied(redirectContext);

        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(403);
    }

    // --- Full ValidatePrincipal flow tests ---

    [Test]
    public async Task ValidatePrincipal_MissingActorClaim_RejectsPrincipal()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Identity has no Actor claim (userId)
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        // Principal should be null (rejected)
        await Assert.That(context.Principal).IsNull();
    }

    [Test]
    public async Task ValidatePrincipal_NonNumericActorClaim_RejectsPrincipal()
    {
        IDatabase redisDb = CreateRedisDb();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        // Actor claim with non-numeric value
        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "not-a-number"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        await Assert.That(context.Principal).IsNull();
    }

    [Test]
    public async Task ValidatePrincipal_InactiveUser_RejectsPrincipal()
    {
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "0"
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        // Inactive user => principal rejected
        await Assert.That(context.Principal).IsNull();
    }

    [Test]
    public async Task ValidatePrincipal_ActiveUserWithCurrentRoles_DoesNotReject()
    {
        string roleString = $"10:{(byte)UserAccountRoles.TenantAdmin}";
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "1",
            ["user:roles:1"] = roleString
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, roleString),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        // Active user with matching roles => principal NOT rejected, NOT renewed
        await Assert.That(context.Principal).IsNotNull();
        await Assert.That(context.ShouldRenew).IsFalse();
    }

    [Test]
    public async Task ValidatePrincipal_ActiveUserWithStaleRoles_RenewsCookie()
    {
        // Database has a new role, but the cookie is stale
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "1",
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantsForUserAsync("ext-123", Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>
            {
                TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: 10, role: UserAccountRoles.TenantAdmin),
                TestDataBuilder.BuildUserTenantRole(userId: 1, tenantId: 20, role: UserAccountRoles.Viewer),
            });

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
            new Claim(ClaimTypes.Role, $"10:{(byte)UserAccountRoles.TenantAdmin}"),
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        // Active user, but roles changed => cookie renewed, principal still valid
        await Assert.That(context.Principal).IsNotNull();
        await Assert.That(context.ShouldRenew).IsTrue();
        List<string> roles = context.Principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        await Assert.That(roles.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ValidatePrincipal_ActiveUserWithNoExternalId_SkipsRoleRefresh()
    {
        // User is active, but missing NameIdentifier — role refresh should be skipped, not crash
        IDatabase redisDb = CreateRedisDb(new Dictionary<string, string>
        {
            ["user:active:1"] = "1",
        });
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        CookiePrincipalValidator validator = CreateValidator(redisDb, tenantRepo, scopeFactory);

        ClaimsIdentity identity = new(new[]
        {
            new Claim(ClaimTypes.Actor, "1"),
            // No NameIdentifier or sub claim
        }, "test");

        CookieValidatePrincipalContext context = CreateValidationContext(identity);
        await validator.ValidatePrincipal(context);

        // Should not crash, should not reject, should not attempt role refresh
        await Assert.That(context.Principal).IsNotNull();
        await tenantRepo.DidNotReceive().GetTenantsForUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static CookieValidatePrincipalContext CreateValidationContext(ClaimsIdentity identity)
    {
        DefaultHttpContext httpContext = new();
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, "Cookies");
        CookieAuthenticationOptions options = new();

        CookieValidatePrincipalContext context = new(
            httpContext,
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)),
            options,
            ticket);

        return context;
    }
}
