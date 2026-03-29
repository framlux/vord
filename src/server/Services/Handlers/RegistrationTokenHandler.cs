// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Tenants;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles registration token operations.
/// </summary>
public sealed class RegistrationTokenHandler : IRegistrationTokenHandler
{
    private readonly DatabaseContext _db;

    /// <summary>
    /// Creates a new instance of the <see cref="RegistrationTokenHandler"/> class.
    /// </summary>
    public RegistrationTokenHandler(DatabaseContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<RegistrationTokenDto>> CreateAsync(int tenantId, int userId, string name, int expiresInDays, int maxUses, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceResult<RegistrationTokenDto>.Error(400, default!);
        }

        if ((expiresInDays < 1) || (expiresInDays > 365))
        {
            return ServiceResult<RegistrationTokenDto>.Error(400, default!);
        }

        if ((maxUses < 1) || (maxUses > 10000))
        {
            return ServiceResult<RegistrationTokenDto>.Error(400, default!);
        }

        string plaintextToken = GenerateToken();
        string tokenHash = ComputeSha256Hash(plaintextToken);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = tokenHash,
            Name = name.Trim(),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(expiresInDays),
            MaxUses = maxUses,
            UsedCount = 0,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };

        token.Id = await _db.InsertWithInt64IdentityAsync(token, token: ct);

        await _db.InsertAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.RegistrationTokenCreated, AuditResourceType.RegistrationToken,
            token.Id.ToString(), new { token.Name, token.MaxUses, ExpiresInDays = expiresInDays }, null), token: ct);

        RegistrationTokenDto dto = new()
        {
            Id = token.Id,
            Name = token.Name,
            Token = plaintextToken,
            ExpiresAt = token.ExpiresAt,
            MaxUses = token.MaxUses,
            UsedCount = token.UsedCount,
            CreatedAt = token.CreatedAt,
            IsRevoked = token.IsRevoked,
        };

        return ServiceResult<RegistrationTokenDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<object>> RevokeAsync(long tokenId, int tenantId, CancellationToken ct)
    {

        int updated = await _db.RegistrationTokens
            .Where(t => t.Id == tokenId && t.TenantId == tenantId && t.IsRevoked == false)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        if (updated == 0)
        {
            return ServiceResult<object>.NotFound();
        }

        await _db.InsertAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.RegistrationTokenRevoked, AuditResourceType.RegistrationToken,
            tokenId.ToString(), null, null), token: ct);

        return ServiceResult<object>.Ok(new { });
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<PaginatedResponse<RegistrationTokenDto>>> ListAsync(int tenantId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1)
        {
            page = 1;
        }

        if ((pageSize < 1) || (pageSize > 100))
        {
            pageSize = 25;
        }

        IQueryable<RegistrationToken> query = _db.RegistrationTokens
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt);

        int totalCount = await query.CountAsync(ct);

        List<RegistrationToken> tokens = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        List<RegistrationTokenDto> dtos = tokens.Select(t => new RegistrationTokenDto
        {
            Id = t.Id,
            Name = t.Name,
            ExpiresAt = t.ExpiresAt,
            MaxUses = t.MaxUses,
            UsedCount = t.UsedCount,
            CreatedAt = t.CreatedAt,
            IsRevoked = t.IsRevoked,
        }).ToList();

        PaginatedResponse<RegistrationTokenDto> result = new()
        {
            Items = dtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        return ServiceResult<PaginatedResponse<RegistrationTokenDto>>.Ok(result);
    }

    private static string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ComputeSha256Hash(string input)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hashBytes);
    }
}
