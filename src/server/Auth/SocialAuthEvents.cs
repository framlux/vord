// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Shared event handlers for social OAuth providers (GitHub, Google, Microsoft).
/// </summary>
public static class SocialAuthEvents
{
    /// <summary>
    /// Handles the OnCreatingTicket event for social OAuth providers.
    /// Looks up or creates the user account, then enriches the identity with application claims.
    /// </summary>
    /// <param name="context">The OAuth creating ticket context.</param>
    /// <returns>Returns an awaitable Task.</returns>
    public static async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        ClaimsIdentity identity = (ClaimsIdentity)context.Principal!.Identity!;
        AuthProviderType provider = ResolveProviderFromScheme(context.Scheme.Name);
        bool success = await PopulateUserClaimsAsync(identity, context.HttpContext, context.HttpContext.RequestAborted, provider);
        if (success == false)
        {
            context.Fail("User account is inactive or not authorized");
        }
    }

    /// <summary>
    /// Maps an OAuth authentication scheme name to an <see cref="AuthProviderType"/>.
    /// </summary>
    internal static AuthProviderType ResolveProviderFromScheme(string schemeName)
    {
        return schemeName?.ToLowerInvariant() switch
        {
            "github" => AuthProviderType.GitHub,
            "google" => AuthProviderType.Google,
            "microsoft" => AuthProviderType.Microsoft,
            _ => AuthProviderType.Unknown,
        };
    }

    /// <summary>
    /// Core logic for populating user claims from the database.
    /// Looks up user by external ID, auto-creates if missing, loads tenant roles, adds claims.
    /// </summary>
    /// <param name="identity">The claims identity to enrich.</param>
    /// <param name="httpContext">The current HTTP context for resolving services.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="authProvider">The authentication provider used for this login.</param>
    /// <returns>Returns true if the user was successfully authenticated; false if denied.</returns>
    public static async Task<bool> PopulateUserClaimsAsync(ClaimsIdentity identity, HttpContext httpContext, CancellationToken ct, AuthProviderType authProvider = AuthProviderType.Unknown)
    {
        string? uniqueId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? identity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(uniqueId))
        {
            return false;
        }

        string email = identity.FindFirst(ClaimTypes.Email)?.Value
            ?? identity.FindFirst("email")?.Value
            ?? string.Empty;

        IDatabaseCache dbCache = httpContext.RequestServices.GetRequiredService<IDatabaseCache>();
        UserAccount? user = await dbCache.GetUserByExternalIdAsync(uniqueId, ct);
        if (user is null)
        {
            // Check whether self-signup is enabled before auto-creating a new account
            IServerSettingsCache settingsCache = httpContext.RequestServices.GetRequiredService<IServerSettingsCache>();
            string? allowSignup = await settingsCache.GetSettingAsync(ServerConfigurationSettingKeys.AllowUserSignup, ct);
            if (string.Equals(allowSignup, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            user = await dbCache.CreateUserAccountAsync(new UserAccount
            {
                CreatedAt = DateTimeOffset.UtcNow,
                ExternalId = uniqueId,
                CreatedByUserId = 1, // System User
                IsActive = true,
                IsGlobalAdmin = false,
                IsSystem = false,
                Username = string.IsNullOrEmpty(email) == false ? email : uniqueId,
                AuthProvider = authProvider,
            }, ct);
        }
        else if ((user.IsActive == false) || user.IsSystem)
        {
            // Remove NameIdentifier so the principal is effectively unauthenticated
            Claim? nameIdClaim = identity.FindFirst(ClaimTypes.NameIdentifier);
            if (nameIdClaim is not null)
            {
                identity.RemoveClaim(nameIdClaim);
            }

            return false;
        }
        else if (string.IsNullOrEmpty(email) == false && string.Equals(user.Username, email, StringComparison.OrdinalIgnoreCase) == false)
        {
            user.Username = email;
            await dbCache.UpdateUserEmailAsync(user.Id, email, ct);
        }

        // Update the auth provider on each login to track the most recent provider used
        if ((authProvider != AuthProviderType.Unknown) && (user.AuthProvider != authProvider))
        {
            await dbCache.UpdateUserAuthProviderAsync(user.Id, authProvider, ct);
        }

        // Retrieve and assign tenant roles
        IEnumerable<UserTenantRole> tenantRoles = await dbCache.GetTenantsForUserAsync(uniqueId, ct);
        foreach (UserTenantRole role in tenantRoles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, $"{role.AssignedTenantId}:{(byte)role.Role}"));
        }

        // Add user account ID as claim
        identity.AddClaim(new Claim(ClaimTypes.Actor, user.Id.ToString()));
        identity.AddClaim(new Claim("iga", user.IsGlobalAdmin.ToString()));

        return true;
    }
}
