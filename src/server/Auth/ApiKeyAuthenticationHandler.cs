// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using System.Text.Encodings.Web;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// An authentication handler that validates API keys provided in the request headers.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The name of the authentication scheme.
    /// </summary>
    public const string SchemeName = "API_Key";

    private readonly IMachineRepository _machineRepository;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="options">The authentication scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="machineRepository">The machine repository.</param>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IMachineRepository machineRepository)
    : base(options, logger, encoder)
    {
        _machineRepository = machineRepository;
    }

    /// <summary>
    /// Handles the authentication process by validating the API key from the request headers.
    /// </summary>
    /// <returns>Returns the authentication result from the challenge</returns>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKeyHeader = null;

        // Try to find one of our machine keys
        if (Request.Headers.TryGetValue("x-api-key", out StringValues nodeKey))
        {
            apiKeyHeader = nodeKey.FirstOrDefault();
            if (string.IsNullOrEmpty(apiKeyHeader))
            {
                Logger.LogWarning("Found x-api-key header but no parsable value");
            }
        }

        // Check if we found any identifiers
        Machine? machine = null;
        if (string.IsNullOrEmpty(apiKeyHeader))
        {
            return AuthenticateResult.Fail("No API key found");
        }
        else
        {
            machine = await _machineRepository.GetMachineByApiKeyAsync(apiKeyHeader, Context.RequestAborted);
            if (machine is null)
            {
                string keyPrefix = apiKeyHeader.Length > 8 ? apiKeyHeader[..8] : apiKeyHeader;
                Logger.LogWarning("Invalid API key attempted (prefix: {KeyPrefix}...)", keyPrefix);

                return AuthenticateResult.Fail("Invalid API key");
            }
        }

        Claim machineIdClaim = new("MachineId", machine.Id.ToString());
        Claim tenantIdClaim = new("TenantId", machine.TenantId.ToString());
        ClaimsIdentity identity = new ([machineIdClaim, tenantIdClaim], Scheme.Name);
        ClaimsPrincipal principal = new (identity);
        AuthenticationTicket ticket = new (principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
