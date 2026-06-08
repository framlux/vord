// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles tenant management operations.
/// </summary>
public sealed partial class TenantHandler : ITenantHandler
{
    private const int MinNameLength = 5;
    private const int MaxNameLength = 100;

    /// <summary>
    /// Matches characters that are not allowed in tenant names.
    /// Blocks HTML/injection characters and ASCII control characters.
    /// </summary>
    [GeneratedRegex("""[<>"'`\\/{}\|\x00-\x1F]""", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex BlockedCharactersRegex();

    private readonly ITenantRepository _tenantRepo;
    private readonly ILogger<TenantHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantHandler"/> class.
    /// </summary>
    public TenantHandler(ITenantRepository tenantRepo, ILogger<TenantHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantRepo = tenantRepo;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<TenantDto>> CreateAsync(string name, string logoUrl, int userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceResult<TenantDto>.BadRequest("Tenant name is required");
        }

        name = name.Trim();

        if ((name.Length < MinNameLength) || (name.Length > MaxNameLength))
        {
            return ServiceResult<TenantDto>.BadRequest("Tenant name must be between 5 and 100 characters");
        }

        if (BlockedCharactersRegex().IsMatch(name))
        {
            return ServiceResult<TenantDto>.BadRequest("Tenant name contains characters that are not allowed");
        }

        Tenant? existing = await _tenantRepo.GetTenantByNameAsync(name, ct);
        if (existing is not null)
        {
            return ServiceResult<TenantDto>.Conflict("A tenant with this name already exists");
        }

        Tenant tenant = await _tenantRepo.CreateTenantAsync(new Tenant
        {
            Name = name,
            ExternalId = Guid.NewGuid().ToString(),
            LogoUrl = logoUrl,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
        }, ct);

        _logger.LogInformation("Tenant '{TenantName}' created by user {UserId}", name, userId);

        TenantDto dto = new()
        {
            Id = tenant.Id,
            Name = tenant.Name,
            LogoUrl = tenant.LogoUrl,
            IsActive = tenant.IsActive,
        };

        return ServiceResult<TenantDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<TenantDto>> GetDetailAsync(int tenantId, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            return ServiceResult<TenantDto>.NotFound();
        }

        TenantDto dto = new()
        {
            Id = tenant.Id,
            Name = tenant.Name,
            LogoUrl = tenant.LogoUrl,
            IsActive = tenant.IsActive,
        };

        return ServiceResult<TenantDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<List<TenantDto>>> ListForUserAsync(bool isGlobalAdmin, List<int> tenantIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        List<Tenant> tenants;
        if (isGlobalAdmin)
        {
            tenants = await _tenantRepo.ListAllTenantsAsync(ct);
        }
        else
        {
            tenants = await _tenantRepo.ListTenantsByIdsAsync(tenantIds, ct);
        }

        List<TenantDto> dtos = tenants.Select(t => new TenantDto
        {
            Id = t.Id,
            Name = t.Name,
            LogoUrl = t.LogoUrl,
            IsActive = t.IsActive,
        }).ToList();

        return ServiceResult<List<TenantDto>>.Ok(dtos);
    }
}
