// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Auth;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Validates the cookie principal on every request. Checks that the user is still active,
/// that the global-admin (iga) claim matches the current DB state, and that role claims are
/// current — refreshing them if they have changed.
/// </summary>
public sealed class CookiePrincipalValidator : CookieAuthenticationEvents
{
    /// <summary>
    /// Redis key prefix for cached user role claims. Full key is "{Prefix}{userId}".
    /// Delegates to <see cref="IRoleCacheInvalidator.RoleCacheKeyPrefix"/> as the shared constant.
    /// </summary>
    public const string RoleCacheKeyPrefix = IRoleCacheInvalidator.RoleCacheKeyPrefix;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly ITenantRepository _tenantRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CookiePrincipalValidator> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CookiePrincipalValidator"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="scopeFactory">The service scope factory for creating database contexts.</param>
    /// <param name="logger">The logger instance.</param>
    public CookiePrincipalValidator(
        IConnectionMultiplexer redis,
        ITenantRepository tenantRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<CookiePrincipalValidator> logger)
    {
        _redis = redis;
        _tenantRepository = tenantRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Validates the principal on every authenticated request. Rejects inactive users,
    /// reconciles the iga claim with current DB state, and refreshes role claims when they
    /// have changed since the cookie was issued.
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

        (bool isActive, bool isGlobalAdmin) = await CheckUserStateAsync(userId, redisDb, context.HttpContext.RequestAborted);
        if (isActive == false)
        {
            context.RejectPrincipal();

            return;
        }

        ReconcileGlobalAdminClaim(context, isGlobalAdmin);

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
    /// Checks whether the user account is still active AND captures the current global-admin
    /// state in the same DB read. Both signals are cached together so reconciling either does
    /// not require a second round-trip. Cache format: "<c>active:iga</c>" where each side is
    /// <c>0</c> or <c>1</c>.
    /// </summary>
    internal async Task<(bool IsActive, bool IsGlobalAdmin)> CheckUserStateAsync(int userId, IDatabase redisDb, CancellationToken ct)
    {
        string cacheKey = $"user:active:{userId}";
        RedisValue cached = await redisDb.StringGetAsync(cacheKey);

        if (cached.HasValue)
        {
            string raw = cached.ToString();
            if (TryParseCachedState(raw, out bool isActive, out bool isGlobalAdmin))
            {
                return (isActive, isGlobalAdmin);
            }
            // Legacy cache format or corrupt — fall through to DB.
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IUserRepository userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        UserAccount? user = await userRepo.GetUserByIdAsync(userId, ct);

        bool active = user is not null && user.IsActive;
        bool admin = user is not null && user.IsGlobalAdmin;
        await redisDb.StringSetAsync(cacheKey, $"{(active ? "1" : "0")}:{(admin ? "1" : "0")}", CacheTtl);

        return (active, admin);
    }

    /// <summary>
    /// Parses the combined active/iga cache string. Accepts the new format
    /// (<c>"active:iga"</c>, e.g. <c>"1:1"</c>) and the legacy single-bit format
    /// (<c>"0"</c> or <c>"1"</c>) for the transition window. Legacy values produce
    /// <c>isGlobalAdmin=false</c> conservatively — a previously-cached admin who has since
    /// been demoted will still appear as non-admin, while an admin who has been retained will
    /// only lose admin until the cache refreshes (≤ 5 minutes), which is the expected refresh
    /// granularity of the cache anyway. Returns <c>false</c> only for genuinely malformed entries.
    /// </summary>
    internal static bool TryParseCachedState(string raw, out bool isActive, out bool isGlobalAdmin)
    {
        isActive = false;
        isGlobalAdmin = false;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Legacy single-bit format ("0" or "1") — parse as active-only.
        if (raw == "0")
        {
            isActive = false;
            isGlobalAdmin = false;

            return true;
        }

        if (raw == "1")
        {
            isActive = true;
            isGlobalAdmin = false;

            return true;
        }

        string[] parts = raw.Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if ((parts[0] != "0") && (parts[0] != "1"))
        {
            return false;
        }

        if ((parts[1] != "0") && (parts[1] != "1"))
        {
            return false;
        }

        isActive = parts[0] == "1";
        isGlobalAdmin = parts[1] == "1";

        return true;
    }

    /// <summary>
    /// Reconciles the cookie's iga claim with the current DB-resolved value. On mismatch the
    /// identity is rebuilt (adding or removing the claim) and the cookie is marked for renewal.
    /// </summary>
    internal static void ReconcileGlobalAdminClaim(CookieValidatePrincipalContext context, bool isGlobalAdminInDb)
    {
        bool cookieClaimsAdmin = AuthClaims.IsUserGlobalAdmin(context.Principal!);
        if (cookieClaimsAdmin == isGlobalAdminInDb)
        {
            return;
        }

        ClaimsIdentity identity = (ClaimsIdentity)context.Principal!.Identity!;
        // Remove every iga claim (case-insensitive compare on name happens at the helper layer;
        // the underlying claim name is the constant from AuthClaims).
        List<Claim> existing = identity.FindAll(AuthClaims.IsGlobalAdmin).ToList();
        foreach (Claim c in existing)
        {
            identity.RemoveClaim(c);
        }

        identity.AddClaim(new Claim(
            AuthClaims.IsGlobalAdmin,
            isGlobalAdminInDb ? AuthClaims.IsGlobalAdminValueTrue : "False"));

        context.ReplacePrincipal(new ClaimsPrincipal(identity));
        context.ShouldRenew = true;
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
        string cacheKey = $"{RoleCacheKeyPrefix}{userId}";
        RedisValue cached = await redisDb.StringGetAsync(cacheKey);

        // Build the expected role string from the database (or cache)
        string currentRoles;
        if (cached.HasValue)
        {
            currentRoles = cached.ToString();
        }
        else
        {
            IEnumerable<UserTenantRole> roles = await _tenantRepository.GetTenantsForUserAsync(externalId, CancellationToken.None);
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
