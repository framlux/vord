// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// DTO for a registration token response.
/// </summary>
public sealed class RegistrationTokenDto
{
    /// <summary>The token ID.</summary>
    public long Id { get; set; }

    /// <summary>The friendly name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The plaintext token (only returned on creation).</summary>
    public string? Token { get; set; }

    /// <summary>When the token was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Whether the token is revoked.</summary>
    public bool IsRevoked { get; set; }
}
