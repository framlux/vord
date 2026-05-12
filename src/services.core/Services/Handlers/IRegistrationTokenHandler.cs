// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles registration token operations.
/// </summary>
public interface IRegistrationTokenHandler
{
    /// <summary>
    /// Creates a new registration token.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="userId">The creating user ID.</param>
    /// <param name="name">The friendly name for the token.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the created token DTO (with plaintext token).</returns>
    Task<ServiceResult<RegistrationTokenDto>> CreateAsync(int tenantId, int userId, string name, CancellationToken ct);

    /// <summary>
    /// Revokes a registration token.
    /// </summary>
    /// <param name="tokenId">The token ID to revoke.</param>
    /// <param name="tenantId">The tenant ID for scoping.</param>
    /// <param name="userId">The ID of the user performing the revocation.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result.</returns>
    Task<ServiceResult<object>> RevokeAsync(long tokenId, int tenantId, int userId, CancellationToken ct);

    /// <summary>
    /// Lists registration tokens for a tenant (paginated).
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the paginated token list.</returns>
    Task<ServiceResult<PaginatedResponse<RegistrationTokenDto>>> ListAsync(int tenantId, int page, int pageSize, CancellationToken ct);
}
