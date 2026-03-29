// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Validates the cookie principal on every request. Checks that the user is still active
/// and that role claims are current, refreshing them if they have changed.
/// </summary>
public sealed class CookiePrincipalValidator : CookieAuthenticationEvents
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabaseCache _databaseCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CookiePrincipalValidator> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CookiePrincipalValidator"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="databaseCache">The database cache.</param>
    /// <param name="scopeFactory">The service scope factory for creating database contexts.</param>
    /// <param name="logger">The logger instance.</param>
    public CookiePrincipalValidator(
        IConnectionMultiplexer redis,
        IDatabaseCache databaseCache,
        IServiceScopeFactory scopeFactory,
        ILogger<CookiePrincipalValidator> logger)
    {
        _redis = redis;
        _databaseCache = databaseCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Validates the principal on every authenticated request. Rejects inactive users and
    /// refreshes role claims when they have changed since the cookie was issued.
    /// </summary>
    /// <param name="context">The cookie validation context.</param>
    /// <returns>Returns an awaitable Task.</returns>
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        Claim? userIdClaim = context.Principal?.FindFirst(ClaimTypes.Actor);
        if ((userIdClaim is null) || (int.TryParse(userIdClaim.Value, out int userId) == false))
        {
            context.RejectPrincipal();

            return;
        }

        IDatabase redisDb = _redis.GetDatabase();

        bool isActive = await CheckUserIsActiveAsync(userId, redisDb, context.HttpContext.RequestAborted);
        if (isActive == false)
        {
            context.RejectPrincipal();

            return;
        }

        string? externalId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Principal?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(externalId) == false)
        {
            await RefreshRoleClaimsIfChangedAsync(context, userId, externalId, redisDb);
        }
    }

    /// <inheritdoc/>
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = 401;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = 403;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks whether the user account is still active, using Redis as a short-lived cache.
    /// </summary>
    internal async Task<bool> CheckUserIsActiveAsync(int userId, IDatabase redisDb, CancellationToken ct)
    {
        string cacheKey = $"user:active:{userId}";
        RedisValue cached = await redisDb.StringGetAsync(cacheKey);

        if (cached.HasValue)
        {
            return cached != "0";
        }

        // Cache miss — query the database
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        UserAccount? user = await db.UserAccounts
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(ct);

        bool isActive = user is not null && user.IsActive;
        await redisDb.StringSetAsync(cacheKey, isActive ? "1" : "0", CacheTtl);

        return isActive;
    }

    /// <summary>
    /// Compares the role claims in the cookie with the current roles in the database.
    /// If they differ, replaces the principal and marks the cookie for renewal.
    /// </summary>
    internal async Task RefreshRoleClaimsIfChangedAsync(
        CookieValidatePrincipalContext context,
        int userId,
        string externalId,
        IDatabase redisDb)
    {
        string cacheKey = $"user:roles:{userId}";
        RedisValue cached = await redisDb.StringGetAsync(cacheKey);

        // Build the expected role string from the database (or cache)
        string currentRoles;
        if (cached.HasValue)
        {
            currentRoles = cached.ToString();
        }
        else
        {
            IEnumerable<UserTenantRole> roles = await _databaseCache.GetTenantsForUserAsync(externalId, CancellationToken.None);
            currentRoles = string.Join(",", roles
                .OrderBy(r => r.AssignedTenantId)
                .Select(r => $"{r.AssignedTenantId}:{(byte)r.Role}"));
            await redisDb.StringSetAsync(cacheKey, currentRoles, CacheTtl);
        }

        // Build the role string from the cookie claims
        IEnumerable<string> cookieRoleClaims = context.Principal!.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .OrderBy(v => v);
        string cookieRoles = string.Join(",", cookieRoleClaims);

        if (string.Equals(currentRoles, cookieRoles, StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation("Refreshing stale role claims for user {UserId}", userId);

        // Rebuild the identity with updated role claims
        ClaimsIdentity identity = (ClaimsIdentity)context.Principal!.Identity!;

        // Remove all existing role claims
        List<Claim> existingRoleClaims = identity.FindAll(ClaimTypes.Role).ToList();
        foreach (Claim claim in existingRoleClaims)
        {
            identity.RemoveClaim(claim);
        }

        // Add the current roles
        if (string.IsNullOrEmpty(currentRoles) == false)
        {
            foreach (string roleClaim in currentRoles.Split(','))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleClaim));
            }
        }

        context.ReplacePrincipal(new ClaimsPrincipal(identity));
        context.ShouldRenew = true;
    }
}
