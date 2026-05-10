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
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// An authentication handler that validates API keys provided in the request headers.
/// Caches validated keys in Redis to avoid database lookups on every request.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The name of the authentication scheme.
    /// </summary>
    public const string SchemeName = "API_Key";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const string CachePrefix = "apikey:";

    private readonly IMachineRepository _machineRepository;
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyAuthenticationHandler"/> class.
    /// </summary>
    /// <param name="options">The authentication scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="machineRepository">The machine repository.</param>
    /// <param name="redis">The Redis connection multiplexer for caching.</param>
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IMachineRepository machineRepository,
        IConnectionMultiplexer redis)
    : base(options, logger, encoder)
    {
        _machineRepository = machineRepository;
        _redis = redis;
    }

    /// <summary>
    /// Handles the authentication process by validating the API key from the request headers.
    /// Uses Redis cache to avoid database lookups on every request.
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

        if (string.IsNullOrEmpty(apiKeyHeader))
        {
            return AuthenticateResult.Fail("No API key found");
        }

        // Check Redis cache first
        (long machineId, int tenantId)? cached = await GetCachedKeyAsync(apiKeyHeader);
        if (cached is not null)
        {
            return BuildSuccessResult(cached.Value.machineId, cached.Value.tenantId);
        }

        // Cache miss — query the database
        Machine? machine = await _machineRepository.GetMachineByApiKeyAsync(apiKeyHeader, Context.RequestAborted);
        if (machine is null)
        {
            string keyPrefix = apiKeyHeader.Length > 8 ? apiKeyHeader[..8] : apiKeyHeader;
            Logger.LogWarning("Invalid API key attempted (prefix: {KeyPrefix}...)", keyPrefix);

            return AuthenticateResult.Fail("Invalid API key");
        }

        // Cache the valid key for future requests
        await SetCachedKeyAsync(apiKeyHeader, machine.Id, machine.TenantId);

        return BuildSuccessResult(machine.Id, machine.TenantId);
    }

    private AuthenticateResult BuildSuccessResult(long machineId, int tenantId)
    {
        Claim machineIdClaim = new("MachineId", machineId.ToString());
        Claim tenantIdClaim = new("TenantId", tenantId.ToString());
        ClaimsIdentity identity = new([machineIdClaim, tenantIdClaim], Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private async Task<(long MachineId, int TenantId)?> GetCachedKeyAsync(string apiKey)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisValue value = await db.StringGetAsync($"{CachePrefix}{apiKey}");
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            string[] parts = ((string)value!).Split(':');
            if ((parts.Length == 2) &&
                long.TryParse(parts[0], out long machineId) &&
                int.TryParse(parts[1], out int tenantId))
            {
                return (machineId, tenantId);
            }
        }
        catch (RedisException ex)
        {
            Logger.LogWarning(ex, "Redis cache read failed for API key auth, falling through to database");
        }

        return null;
    }

    private async Task SetCachedKeyAsync(string apiKey, long machineId, int tenantId)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            await db.StringSetAsync($"{CachePrefix}{apiKey}", $"{machineId}:{tenantId}", CacheTtl);
        }
        catch (RedisException ex)
        {
            Logger.LogWarning(ex, "Redis cache write failed for API key auth");
        }
    }

    /// <summary>
    /// Invalidates the cached API key entry. Call when a key is revoked or a machine is deleted.
    /// </summary>
    /// <param name="apiKey">The API key to invalidate from cache.</param>
    public async Task InvalidateCachedKeyAsync(string apiKey)
    {
        try
        {
            IDatabase db = _redis.GetDatabase();
            await db.KeyDeleteAsync($"{CachePrefix}{apiKey}");
        }
        catch (RedisException ex)
        {
            Logger.LogWarning(ex, "Redis cache invalidation failed for API key");
        }
    }
}
