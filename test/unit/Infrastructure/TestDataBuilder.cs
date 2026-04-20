// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using System.Security.Cryptography;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Fluent factory for creating test entities with sensible defaults.
/// </summary>
public static class TestDataBuilder
{
    private static int _userCounter;
    private static int _tenantCounter;
    private static int _machineCounter;

    /// <summary>
    /// Builds a <see cref="UserAccount"/> with sensible defaults.
    /// </summary>
    public static UserAccount BuildUser(
        string? externalId = null,
        string? username = null,
        bool isGlobalAdmin = false,
        bool isActive = true)
    {
        int n = Interlocked.Increment(ref _userCounter);

        return new UserAccount
        {
            ExternalId = externalId ?? $"ext-user-{n}",
            Username = username ?? $"testuser{n}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = isActive,
            IsSystem = false,
            IsGlobalAdmin = isGlobalAdmin
        };
    }

    /// <summary>
    /// Builds a <see cref="Tenant"/> with sensible defaults.
    /// </summary>
    public static Tenant BuildTenant(
        string? name = null,
        string? externalId = null,
        int createdByUserId = 1)
    {
        int n = Interlocked.Increment(ref _tenantCounter);

        return new Tenant
        {
            ExternalId = externalId ?? $"ext-tenant-{n}",
            Name = name ?? $"Test Tenant {n}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
            IsActive = true,
            LogoUrl = ""
        };
    }

    /// <summary>
    /// Builds a <see cref="Machine"/> with sensible defaults.
    /// </summary>
    public static Machine BuildMachine(
        int tenantId = 1,
        string? apiKeyHash = null,
        string? hostname = null,
        long registrationTokenId = 1)
    {
        int n = Interlocked.Increment(ref _machineCounter);

        return new Machine
        {
            ApiKeyHash = apiKeyHash ?? $"{n:D64}",
            Name = hostname ?? $"machine-{n}",
            SerialNumber = $"sn-{n:D8}",
            SystemId = $"sid-{n:D8}",
            AssetTagNumber = null,
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = registrationTokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
    }

    /// <summary>
    /// Builds a <see cref="TenantSubscription"/> with sensible defaults.
    /// </summary>
    public static TenantSubscription BuildSubscription(
        int tenantId = 1,
        SubscriptionTier tier = SubscriptionTier.Free,
        SubscriptionStatus status = SubscriptionStatus.Active,
        int? machineLimit = 3,
        int retentionDays = 7)
    {
        return new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = status,
            MachineLimit = machineLimit,
            RetentionDays = retentionDays,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Builds a <see cref="UserTenantRole"/> with sensible defaults.
    /// </summary>
    public static UserTenantRole BuildUserTenantRole(
        int userId = 1,
        int tenantId = 1,
        UserAccountRoles role = UserAccountRoles.TenantAdmin,
        int assignedByUserId = 1)
    {
        return new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = tenantId,
            Role = role,
            AssignedByUserId = assignedByUserId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
    }

    /// <summary>
    /// Builds a <see cref="TenantInvitation"/> with sensible defaults.
    /// </summary>
    public static TenantInvitation BuildInvitation(
        int tenantId = 1,
        string? email = null,
        InvitationStatus status = InvitationStatus.Pending,
        int invitedByUserId = 1)
    {
        int n = Interlocked.Increment(ref _userCounter);

        return new TenantInvitation
        {
            TenantId = tenantId,
            Email = email ?? $"invite{n}@example.com",
            TokenHash = Guid.NewGuid().ToString("N"),
            Role = UserAccountRoles.Viewer,
            Status = status,
            InvitedByUserId = invitedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
    }

    /// <summary>
    /// Builds a <see cref="MachineTelemetry"/> with sensible defaults.
    /// </summary>
    public static MachineTelemetry BuildMachineTelemetry(
        long machineId = 1,
        int tenantId = 1,
        short telemetryType = 1,
        string? payload = null)
    {
        return new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = telemetryType,
            Payload = payload ?? """{"cpu": 42}""",
            ReceivedAt = DateTimeOffset.UtcNow,
            SourceEventId = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>
    /// Builds a <see cref="TenantOidcConfiguration"/> with sensible defaults.
    /// </summary>
    public static TenantOidcConfiguration BuildTenantOidcConfiguration(
        int tenantId = 1,
        string authority = "https://login.example.com",
        string clientId = "test-client-id",
        string clientSecret = "encrypted-secret",
        bool isEnabled = true)
    {
        return new TenantOidcConfiguration
        {
            TenantId = tenantId,
            Authority = authority,
            ClientId = clientId,
            ClientSecret = clientSecret,
            MetadataAddress = null,
            EmailDomain = "example.com",
            IsEnabled = isEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Builds a <see cref="UserSigningKey"/> with sensible defaults.
    /// </summary>
    public static UserSigningKey BuildSigningKey(
        int userId = 1,
        int tenantId = 1,
        string? label = null,
        string? publicKey = null,
        string? fingerprint = null)
    {
        int n = Interlocked.Increment(ref _userCounter);
        byte[] keyBytes = publicKey is not null
            ? Convert.FromBase64String(publicKey)
            : new byte[32];

        if (publicKey is null)
        {
            // Fill with deterministic bytes based on counter.
            for (int i = 0; i < 32; i++)
            {
                keyBytes[i] = (byte)((n + i) % 256);
            }
        }

        string base64Key = publicKey ?? Convert.ToBase64String(keyBytes);
        string fp = fingerprint ?? Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(keyBytes));

        return new UserSigningKey
        {
            UserId = userId,
            TenantId = tenantId,
            Label = label ?? $"Test Key {n}",
            PublicKey = base64Key,
            PublicKeyFingerprint = fp,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds a <see cref="RemoteCommand"/> with sensible defaults.
    /// </summary>
    public static RemoteCommand BuildRemoteCommand(
        long machineId = 1,
        int tenantId = 1,
        int userId = 1,
        int signingKeyId = 1,
        string? commandId = null,
        string commandType = "reboot",
        RemoteCommandStatus status = RemoteCommandStatus.Pending)
    {
        return new RemoteCommand
        {
            CommandId = commandId ?? Guid.NewGuid().ToString("D"),
            TenantId = tenantId,
            MachineId = machineId,
            UserId = userId,
            SigningKeyId = signingKeyId,
            CommandType = commandType,
            Params = null,
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String(new byte[64]),
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds a <see cref="MachineStateSummary"/> with sensible defaults.
    /// </summary>
    public static MachineStateSummary BuildMachineStateSummary(
        long machineId = 1,
        int tenantId = 1,
        string? name = null,
        int? cpuPercent = null,
        int? memoryPercent = null,
        short healthStatus = 0,
        DateTimeOffset? lastSeenAt = null)
    {
        return new MachineStateSummary
        {
            MachineId = machineId,
            TenantId = tenantId,
            Name = name ?? $"machine-{machineId}",
            OperatingSystem = 0,
            MachineType = 0,
            Hostname = $"host-{machineId}",
            CpuUsagePercent = cpuPercent,
            MemoryUsagePercent = memoryPercent,
            HealthStatus = healthStatus,
            LastSeenAt = lastSeenAt,
        };
    }

    /// <summary>
    /// Builds an <see cref="AlertRule"/> with sensible defaults.
    /// </summary>
    public static AlertRule BuildAlertRule(
        int tenantId = 1,
        AlertMetric metric = AlertMetric.CpuUsage,
        AlertOperator op = AlertOperator.GreaterThan,
        decimal threshold = 80m,
        AlertSeverity severity = AlertSeverity.Warning,
        bool isCustom = true,
        bool isEnabled = true,
        int durationMinutes = 0,
        bool notifyEmail = false,
        bool notifyWebhook = false,
        int createdByUserId = 1)
    {
        int n = Interlocked.Increment(ref _machineCounter);

        return new AlertRule
        {
            TenantId = tenantId,
            Name = $"Alert Rule {n}",
            Description = $"Test alert rule {n}",
            Metric = metric,
            Operator = op,
            Threshold = threshold,
            DurationMinutes = durationMinutes,
            Severity = severity,
            IsEnabled = isEnabled,
            NotifyEmail = notifyEmail,
            NotifyWebhook = notifyWebhook,
            IsCustom = isCustom,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds an <see cref="AlertEvent"/> with sensible defaults.
    /// </summary>
    public static AlertEvent BuildAlertEvent(
        int alertRuleId = 1,
        int tenantId = 1,
        long machineId = 1,
        AlertSeverity severity = AlertSeverity.Warning,
        AlertEventStatus status = AlertEventStatus.Triggered,
        string message = "Test alert",
        string? details = null)
    {
        return new AlertEvent
        {
            AlertRuleId = alertRuleId,
            TenantId = tenantId,
            MachineId = machineId,
            Severity = severity,
            Message = message,
            Details = details,
            Status = status,
            TriggeredAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds a <see cref="WebhookEndpoint"/> with sensible defaults.
    /// </summary>
    public static WebhookEndpoint BuildWebhookEndpoint(
        int tenantId = 1,
        string? name = null,
        string url = "https://hooks.example.com/test",
        string? secret = null,
        bool isEnabled = true,
        int createdByUserId = 1)
    {
        int n = Interlocked.Increment(ref _machineCounter);

        return new WebhookEndpoint
        {
            TenantId = tenantId,
            Name = name ?? $"Test Webhook {n}",
            Url = url,
            Secret = secret ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            IsEnabled = isEnabled,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Builds a <see cref="MachineAuthorizedKey"/> with sensible defaults.
    /// </summary>
    public static MachineAuthorizedKey BuildMachineAuthorizedKey(
        long machineId = 1,
        int signingKeyId = 1,
        int tenantId = 1,
        int authorizedByUserId = 1)
    {
        return new MachineAuthorizedKey
        {
            MachineId = machineId,
            SigningKeyId = signingKeyId,
            TenantId = tenantId,
            AuthorizedAt = DateTimeOffset.UtcNow,
            AuthorizedByUserId = authorizedByUserId,
        };
    }
}
