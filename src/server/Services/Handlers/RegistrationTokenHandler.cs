// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles registration token operations.
/// </summary>
public sealed class RegistrationTokenHandler : IRegistrationTokenHandler
{
    private readonly IRegistrationTokenRepository _tokenRepo;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="RegistrationTokenHandler"/> class.
    /// </summary>
    public RegistrationTokenHandler(
        IRegistrationTokenRepository tokenRepo,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog)
    {
        ArgumentNullException.ThrowIfNull(tokenRepo);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);

        _tokenRepo = tokenRepo;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<RegistrationTokenDto>> CreateAsync(int tenantId, int userId, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ServiceResult<RegistrationTokenDto>.BadRequest("Registration token name is required");
        }

        string plaintextToken = GenerateToken();
        string tokenHash = ComputeSha256Hash(plaintextToken);

        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = tokenHash,
            Name = name.Trim(),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _tokenRepo.CreateRegistrationTokenAsync(token, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.RegistrationTokenCreated, AuditResourceType.RegistrationToken,
            token.Id.ToString(), new { token.Name }, null), ct);

        await transaction.CommitAsync(ct);

        RegistrationTokenDto dto = new()
        {
            Id = token.Id,
            Name = token.Name,
            Token = plaintextToken,
            CreatedAt = token.CreatedAt,
            IsRevoked = token.IsRevoked,
        };

        return ServiceResult<RegistrationTokenDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<object>> RevokeAsync(long tokenId, int tenantId, int userId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _tokenRepo.RevokeRegistrationTokenAsync(tokenId, tenantId, ct);

        if (updated == 0)
        {
            return ServiceResult<object>.NotFound();
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.RegistrationTokenRevoked, AuditResourceType.RegistrationToken,
            tokenId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

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

        int totalCount = await _tokenRepo.CountRegistrationTokensForTenantAsync(tenantId, ct);

        List<RegistrationToken> tokens = await _tokenRepo.GetRegistrationTokensForTenantAsync(
            tenantId, (page - 1) * pageSize, pageSize, ct);

        List<RegistrationTokenDto> dtos = tokens.Select(t => new RegistrationTokenDto
        {
            Id = t.Id,
            Name = t.Name,
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
