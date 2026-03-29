// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
{
    /// <inheritdoc/>
    public async Task<TenantInvitation> CreateInvitationAsync(TenantInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        try
        {
            _logger.LogDebug("Creating invitation for email {Email} in tenant {TenantId}", MaskEmail(invitation.Email), invitation.TenantId);
            invitation.Id = await _db.InsertWithInt32IdentityAsync(invitation, token: cancellationToken);
            _logger.LogInformation("Created invitation {InvitationId} for email {Email} in tenant {TenantId}", invitation.Id, MaskEmail(invitation.Email), invitation.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create invitation for email {Email} in tenant {TenantId}", MaskEmail(invitation.Email), invitation.TenantId);
            throw;
        }

        return invitation;
    }

    /// <inheritdoc/>
    public async Task<TenantInvitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        TenantInvitation? invitation = null;
        string tokenHash = HashToken(token);

        try
        {
            _logger.LogDebug("Retrieving invitation by token hash");
            invitation = await (from i in _db.TenantInvitations
                    join t in _db.Tenants on i.TenantId equals t.Id
                    join u in _db.UserAccounts on i.InvitedByUserId equals u.Id
                    where i.TokenHash == tokenHash
                    select new TenantInvitation
                    {
                        Id = i.Id,
                        TenantId = i.TenantId,
                        Tenant = t,
                        Email = i.Email,
                        TokenHash = i.TokenHash,
                        Role = i.Role,
                        Status = i.Status,
                        InvitedByUserId = i.InvitedByUserId,
                        InvitedByUser = u,
                        AcceptedByUserId = i.AcceptedByUserId,
                        CreatedAt = i.CreatedAt,
                        ExpiresAt = i.ExpiresAt,
                        AcceptedAt = i.AcceptedAt,
                        RevokedAt = i.RevokedAt,
                    }).FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Retrieved invitation by token hash: {Found}", invitation is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve invitation by token hash");
        }

        return invitation;
    }

    /// <summary>
    /// Computes the SHA-256 hash of a token as a lowercase hex string.
    /// </summary>
    /// <param name="token">The plaintext token to hash.</param>
    /// <returns>The hex-encoded SHA-256 hash.</returns>
    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToHexStringLower(hash);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<TenantInvitation>> GetInvitationsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        IEnumerable<TenantInvitation> invitations = [];

        try
        {
            _logger.LogDebug("Retrieving invitations for tenant {TenantId}", tenantId);
            invitations = await _db.TenantInvitations
                .Where(i => i.TenantId == tenantId)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync(cancellationToken);
            _logger.LogInformation("Retrieved {Count} invitations for tenant {TenantId}", invitations.Count(), tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve invitations for tenant {TenantId}", tenantId);
        }

        return invitations;
    }

    /// <inheritdoc/>
    public async Task<TenantInvitation?> GetPendingInvitationByEmailAndTenantAsync(string email, int tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        TenantInvitation? invitation = null;

        try
        {
            _logger.LogDebug("Checking for pending invitation for email {Email} in tenant {TenantId}", MaskEmail(email), tenantId);
            invitation = await _db.TenantInvitations
                .Where(i => i.Email == email &&
                             i.TenantId == tenantId &&
                             i.Status == InvitationStatus.Pending)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for pending invitation for email {Email} in tenant {TenantId}", MaskEmail(email), tenantId);
        }

        return invitation;
    }

    /// <inheritdoc/>
    public async Task UpdateInvitationStatusAsync(int invitationId, InvitationStatus status, int? acceptedByUserId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Updating invitation {InvitationId} to status {Status}", invitationId, status);

            if (status == InvitationStatus.Accepted)
            {
                await _db.TenantInvitations
                    .Where(i => i.Id == invitationId)
                    .Set(i => i.Status, status)
                    .Set(i => i.AcceptedByUserId, acceptedByUserId)
                    .Set(i => i.AcceptedAt, DateTimeOffset.UtcNow)
                    .UpdateAsync(cancellationToken);
            }
            else
            {
                await _db.TenantInvitations
                    .Where(i => i.Id == invitationId)
                    .Set(i => i.Status, status)
                    .UpdateAsync(cancellationToken);
            }

            _logger.LogInformation("Updated invitation {InvitationId} to status {Status}", invitationId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update invitation {InvitationId} to status {Status}", invitationId, status);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task RevokeInvitationAsync(int invitationId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Revoking invitation {InvitationId}", invitationId);
            await _db.TenantInvitations
                .Where(i => i.Id == invitationId)
                .Set(i => i.Status, InvitationStatus.Revoked)
                .Set(i => i.RevokedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(cancellationToken);
            _logger.LogInformation("Revoked invitation {InvitationId}", invitationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke invitation {InvitationId}", invitationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<UserAccount?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        UserAccount? user = await _db.UserAccounts
            .Where(u => u.Username.ToLower() == email.ToLower() && u.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        return user;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<UserTenantRole>> GetMembersForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<UserTenantRole> roles = await (from utr in _db.UserTenantRoles
                join ua in _db.UserAccounts on utr.UserId equals ua.Id
                where utr.AssignedTenantId == tenantId && utr.IsActive && ua.IsActive
                select new UserTenantRole
                {
                    UserId = utr.UserId,
                    User = ua,
                    AssignedTenantId = utr.AssignedTenantId,
                    Role = utr.Role,
                    AssignedByUserId = utr.AssignedByUserId,
                    AssignedAt = utr.AssignedAt,
                    IsActive = utr.IsActive,
                }).ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<bool> DisableUserTenantRoleAsync(int userId, int tenantId, int disabledByUserId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Disabling UserTenantRole for user {UserId} in tenant {TenantId}", userId, tenantId);
            int affected = await _db.UserTenantRoles
                .Where(utr => utr.UserId == userId &&
                               utr.AssignedTenantId == tenantId &&
                               utr.IsActive)
                .Set(utr => utr.IsActive, false)
                .Set(utr => utr.DisabledByUserId, disabledByUserId)
                .Set(utr => utr.DisabledAt, DateTimeOffset.UtcNow)
                .UpdateAsync(cancellationToken);

            _logger.LogInformation("Disabled {Count} UserTenantRole(s) for user {UserId} in tenant {TenantId}", affected, userId, tenantId);

            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable UserTenantRole for user {UserId} in tenant {TenantId}", userId, tenantId);
            throw;
        }
    }

    /// <summary>
    /// Masks an email address for safe logging (e.g., "j***@example.com").
    /// </summary>
    private static string MaskEmail(string email)
    {
        int atIndex = email.IndexOf('@');
        if (atIndex <= 0)
        {
            return "***";
        }

        return $"{email[0]}***{email[atIndex..]}";
    }
}
