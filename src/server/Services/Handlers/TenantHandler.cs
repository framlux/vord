// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Tenants;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles tenant management operations.
/// </summary>
public sealed class TenantHandler : ITenantHandler
{
    private readonly IDatabaseCache _databaseCache;
    private readonly DatabaseContext _db;
    private readonly ILogger<TenantHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantHandler"/> class.
    /// </summary>
    public TenantHandler(IDatabaseCache databaseCache, DatabaseContext db, ILogger<TenantHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(databaseCache);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        _databaseCache = databaseCache;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<TenantDto>> CreateAsync(string name, string logoUrl, int userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceResult<TenantDto>.Error(400, default!);
        }

        Tenant? existing = await _databaseCache.GetTenantByNameAsync(name, ct);
        if (existing is not null)
        {
            return ServiceResult<TenantDto>.Error(409, default!);
        }

        Tenant tenant = await _databaseCache.CreateTenantAsync(new Tenant
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
        Tenant? tenant = await _databaseCache.GetTenantByIdAsync(tenantId, ct);
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
            tenants = await _db.Tenants
                .OrderBy(t => t.Name)
                .ToListAsync(ct);
        }
        else
        {
            tenants = await _db.Tenants
                .Where(t => tenantIds.Contains(t.Id))
                .OrderBy(t => t.Name)
                .ToListAsync(ct);
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
