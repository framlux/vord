// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;

/// <summary>
/// Authenticated user data returned to the UI.
/// </summary>
public sealed class UserDto
{
    /// <summary>
    /// The user's internal ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The user's display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The user's email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The user's avatar URL.
    /// </summary>
    public string Avatar { get; set; } = "/avatars/default.jpg";

    /// <summary>
    /// Whether the user is a global administrator.
    /// </summary>
    public bool IsGlobalAdmin { get; set; }

    /// <summary>
    /// The user's unique identifier from the identity provider.
    /// </summary>
    public string UniqueId { get; set; } = string.Empty;

    /// <summary>
    /// The user's tenants.
    /// </summary>
    public List<UserTenantDto> Tenants { get; set; } = new();

    /// <summary>
    /// Whether the user needs to complete onboarding (has no tenant memberships).
    /// </summary>
    public bool NeedsOnboarding { get; set; }

    /// <summary>
    /// The currently active tenant ID (resolved from cookie or first role claim).
    /// </summary>
    public int? ActiveTenantId { get; set; }

    /// <summary>
    /// Helper to map from a <see cref="ClaimsPrincipal"/>.
    /// </summary>
    public static UserDto FromPrincipal(ClaimsPrincipal user, ILogger logger)
    {
        string uniqueId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? "unknown";
        string displayName = user.FindFirst("display_name")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? string.Empty;
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = user.FindFirst(ClaimTypes.GivenName)?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(displayName))
            {
                logger.LogWarning("User {UniqueId} does not have display_name claim", uniqueId);
                displayName = "Unknown User";
            }
        }

        string email = user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? string.Empty;

        string picture = user.FindFirst("picture")?.Value ?? "/avatars/default.jpg";

        return new UserDto
        {
            UniqueId = uniqueId,
            Name = displayName,
            Email = email,
            Avatar = picture,
            Id = 0,
            IsGlobalAdmin = false,
        };
    }
}
