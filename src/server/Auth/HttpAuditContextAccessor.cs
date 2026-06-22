// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// HTTP-aware implementation of <see cref="IAuditContextAccessor"/> that extracts the client
/// IP address from the active <see cref="HttpContext"/>. When the connection remote address is
/// available it is returned as a string; when no HTTP context is active (e.g., during
/// background job execution) this returns <see langword="null"/> so the audit log IP column
/// is left unset.
/// </summary>
public sealed class HttpAuditContextAccessor : IAuditContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of <see cref="HttpAuditContextAccessor"/>.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP context.</param>
    public HttpAuditContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc/>
    public string? GetClientIp()
    {
        return _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    }
}
