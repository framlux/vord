// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Notifications;
using Framlux.FleetManagement.Server.Services.Security;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles invitation business logic.
/// </summary>
public sealed class InvitationHandler : IInvitationHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IInvitationRepository _invitationRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IEmailService _emailService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly SubscriptionOptions _subscriptionOptions;
    private readonly IRoleCacheInvalidator _roleCacheInvalidator;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationHandler"/> class.
    /// </summary>
    public InvitationHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        IInvitationRepository invitationRepository,
        ITenantRepository tenantRepository,
        ISubscriptionRepository subscriptionRepository,
        IEmailService emailService,
        ISubscriptionService subscriptionService,
        IOptions<SubscriptionOptions> subscriptionOptions,
        IRoleCacheInvalidator roleCacheInvalidator)
    {
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _invitationRepository = invitationRepository;
        _tenantRepository = tenantRepository;
        _subscriptionRepository = subscriptionRepository;
        _emailService = emailService;
        _subscriptionService = subscriptionService;
        _subscriptionOptions = subscriptionOptions.Value;
        _roleCacheInvalidator = roleCacheInvalidator;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<InvitationCreateResult>> CreateAsync(
        string email,
        string? role,
        int? tenantId,
        int userId,
        string baseUrl,
        CancellationToken ct)
    {
        string normalizedEmail = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(normalizedEmail) || normalizedEmail.Contains('@') == false)
        {
            return ServiceResult<InvitationCreateResult>.Error(400,
                new InvitationCreateResult { ErrorMessage = "A valid email address is required" });
        }

        if (tenantId is null)
        {
            return ServiceResult<InvitationCreateResult>.Error(401,
                new InvitationCreateResult { ErrorMessage = "Unauthorized" });
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            return ServiceResult<InvitationCreateResult>.Error(402,
                new InvitationCreateResult { ErrorMessage = "Upgrade to Pro or Team to invite team members" });
        }

        TenantInvitation? existing = await _invitationRepository.GetPendingInvitationByEmailAndTenantAsync(normalizedEmail, tenantId.Value, ct);
        if (existing is not null)
        {
            return ServiceResult<InvitationCreateResult>.Error(409,
                new InvitationCreateResult { ErrorMessage = "A pending invitation already exists for this email" });
        }

        IEnumerable<UserTenantRole> members = await _tenantRepository.GetMembersForTenantAsync(tenantId.Value, ct);
        bool alreadyMember = members.Any(m => m.User is not null &&
            string.Equals(m.User.Username, normalizedEmail, StringComparison.OrdinalIgnoreCase));
        if (alreadyMember)
        {
            return ServiceResult<InvitationCreateResult>.Error(409,
                new InvitationCreateResult { ErrorMessage = "This user is already a member of your organization" });
        }

        UserAccountRoles assignedRole = UserAccountRoles.Viewer;
        if (string.IsNullOrEmpty(role) == false && Enum.TryParse<UserAccountRoles>(role, true, out UserAccountRoles parsed))
        {
            assignedRole = parsed;
        }

        // Non-Team tiers get TenantAdmin role forced for all invitations
        if (subscription.Tier != SubscriptionTier.Team)
        {
            assignedRole = UserAccountRoles.TenantAdmin;
        }

        string token = RandomNumberGenerator.GetHexString(64, true);
        string tokenHash = HashToken(token);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        TenantInvitation invitation = await _invitationRepository.CreateInvitationAsync(new TenantInvitation
        {
            TenantId = tenantId.Value,
            Email = normalizedEmail,
            TokenHash = tokenHash,
            Role = assignedRole,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
        }, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.MemberInvited, AuditResourceType.Invitation,
            invitation.Id.ToString(), new { invitation.Email, Role = assignedRole.ToString() }, null), ct);

        await transaction.CommitAsync(ct);

        string acceptUrl = $"{baseUrl}/invitations/accept?token={token}";

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
        string tenantName = tenant?.Name ?? "your organization";

        await _emailService.SendInvitationEmailAsync(normalizedEmail, tenantName, "A team member", acceptUrl, ct);

        return ServiceResult<InvitationCreateResult>.Ok(new InvitationCreateResult
        {
            Id = invitation.Id,
            Email = invitation.Email,
            Token = token,
            AcceptUrl = acceptUrl,
            ExpiresAt = invitation.ExpiresAt,
            Status = invitation.Status.ToString(),
        });
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<InvitationAcceptResult>> AcceptAsync(
        string token,
        string userEmail,
        int userId,
        string uniqueId,
        CancellationToken ct)
    {
        TenantInvitation? invitation = await _invitationRepository.GetInvitationByTokenAsync(token, ct);
        if (invitation is null)
        {
            return ServiceResult<InvitationAcceptResult>.NotFound();
        }

        if (invitation.Status != InvitationStatus.Pending)
        {
            return ServiceResult<InvitationAcceptResult>.Error(400,
                new InvitationAcceptResult { ErrorMessage = $"This invitation has already been {invitation.Status.ToString().ToLowerInvariant()}" });
        }

        if (invitation.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return ServiceResult<InvitationAcceptResult>.Error(400,
                new InvitationAcceptResult { ErrorMessage = "This invitation has expired" });
        }

        if (string.Equals(userEmail, invitation.Email, StringComparison.OrdinalIgnoreCase) == false)
        {
            return ServiceResult<InvitationAcceptResult>.Error(403,
                new InvitationAcceptResult { ErrorMessage = "Your email address does not match this invitation" });
        }

        if (userId == 0)
        {
            return ServiceResult<InvitationAcceptResult>.Error(401,
                new InvitationAcceptResult { ErrorMessage = "Unauthorized" });
        }

        IEnumerable<UserTenantRole> existingRoles = await _tenantRepository.GetTenantsForUserAsync(uniqueId, ct);
        bool alreadyMember = existingRoles.Any(r => r.AssignedTenantId == invitation.TenantId);
        if (alreadyMember)
        {
            return ServiceResult<InvitationAcceptResult>.Error(409,
                new InvitationAcceptResult { ErrorMessage = "You are already a member of this organization" });
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool personalTenantProvisioned = false;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        if (existingRoles.Any() == false)
        {
            Tenant personalTenant = await _tenantRepository.CreateTenantAsync(new Tenant
            {
                Name = $"{userEmail}'s Organization",
                ExternalId = Guid.NewGuid().ToString(),
                CreatedAt = now,
                CreatedByUserId = userId,
                IsActive = true,
                LogoUrl = string.Empty,
            }, ct);

            // Create the free subscription within the same transaction so the FK on TenantId
            // can see the uncommitted Tenant row.
            await _subscriptionRepository.CreateTenantSubscriptionAsync(new TenantSubscription
            {
                TenantId = personalTenant.Id,
                Tier = SubscriptionTier.Free,
                Status = SubscriptionStatus.Active,
                MachineLimit = _subscriptionOptions.FreeTierMachineLimit,
                RetentionDays = _subscriptionOptions.FreeTierRetentionDays,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);

            await _tenantRepository.CreateUserTenantRoleAsync(new UserTenantRole
            {
                UserId = userId,
                AssignedTenantId = personalTenant.Id,
                Role = UserAccountRoles.TenantAdmin,
                AssignedByUserId = userId,
                AssignedAt = now,
                IsActive = true,
            }, ct);

            personalTenantProvisioned = true;
        }

        await _tenantRepository.CreateUserTenantRoleAsync(new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = invitation.TenantId,
            Role = invitation.Role,
            AssignedByUserId = invitation.InvitedByUserId,
            AssignedAt = now,
            IsActive = true,
        }, ct);

        await _invitationRepository.UpdateInvitationStatusAsync(invitation.Id, InvitationStatus.Accepted, userId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            invitation.TenantId, userId, null,
            AuditAction.MemberInvitationAccepted, AuditResourceType.Invitation,
            invitation.Id.ToString(), new { invitation.Email }, null), ct);

        await transaction.CommitAsync(ct);

        // Invalidate the cached role claims after the transaction commits
        await _roleCacheInvalidator.InvalidateAsync(userId, ct);

        return ServiceResult<InvitationAcceptResult>.Ok(new InvitationAcceptResult
        {
            TenantId = invitation.TenantId,
            PersonalTenantProvisioned = personalTenantProvisioned,
        });
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<InvitationRevokeResult>> RevokeAsync(
        int invitationId,
        int? tenantId,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<InvitationRevokeResult>.Error(401,
                new InvitationRevokeResult { ErrorMessage = "Unauthorized" });
        }

        IEnumerable<TenantInvitation> invitations = await _invitationRepository.GetInvitationsForTenantAsync(tenantId.Value, ct);
        TenantInvitation? invitation = invitations.FirstOrDefault(i => i.Id == invitationId);

        if (invitation is null)
        {
            return ServiceResult<InvitationRevokeResult>.NotFound();
        }

        if (invitation.Status != InvitationStatus.Pending)
        {
            return ServiceResult<InvitationRevokeResult>.Error(400,
                new InvitationRevokeResult { ErrorMessage = "Only pending invitations can be revoked" });
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _invitationRepository.RevokeInvitationAsync(invitationId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.MemberInvitationRevoked, AuditResourceType.Invitation,
            invitationId.ToString(), new { invitation.Email }, null), ct);

        await transaction.CommitAsync(ct);

        return ServiceResult<InvitationRevokeResult>.Ok(new InvitationRevokeResult());
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<InvitationResendResult>> ResendAsync(
        int invitationId,
        int? tenantId,
        int userId,
        string inviterEmail,
        string baseUrl,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<InvitationResendResult>.Error(401,
                new InvitationResendResult { ErrorMessage = "Unauthorized" });
        }

        IEnumerable<TenantInvitation> invitations = await _invitationRepository.GetInvitationsForTenantAsync(tenantId.Value, ct);
        TenantInvitation? oldInvitation = invitations.FirstOrDefault(i => i.Id == invitationId);

        if (oldInvitation is null)
        {
            return ServiceResult<InvitationResendResult>.NotFound();
        }

        if (oldInvitation.Status != InvitationStatus.Pending)
        {
            return ServiceResult<InvitationResendResult>.Error(400,
                new InvitationResendResult { ErrorMessage = "Only pending invitations can be resent" });
        }

        string token = RandomNumberGenerator.GetHexString(64, true);
        string tokenHash = HashToken(token);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _invitationRepository.RevokeInvitationAsync(invitationId, ct);

        TenantInvitation newInvitation = await _invitationRepository.CreateInvitationAsync(new TenantInvitation
        {
            TenantId = tenantId.Value,
            Email = oldInvitation.Email,
            TokenHash = tokenHash,
            Role = oldInvitation.Role,
            Status = InvitationStatus.Pending,
            InvitedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
        }, ct);

        await transaction.CommitAsync(ct);

        string acceptUrl = $"{baseUrl}/invitations/accept?token={token}";

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
        string tenantName = tenant?.Name ?? "your organization";

        await _emailService.SendInvitationEmailAsync(oldInvitation.Email, tenantName, inviterEmail, acceptUrl, ct);

        return ServiceResult<InvitationResendResult>.Ok(new InvitationResendResult
        {
            Id = newInvitation.Id,
            Email = newInvitation.Email,
            Token = token,
            AcceptUrl = acceptUrl,
            ExpiresAt = newInvitation.ExpiresAt,
            Status = newInvitation.Status.ToString(),
        });
    }

    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToHexStringLower(hash);
    }
}
