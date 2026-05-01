// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the type of action recorded in an audit log entry.
/// </summary>
public enum AuditAction : short
{
    /// <summary>A user logged in.</summary>
    UserLogin = 1,
    /// <summary>A user logged out.</summary>
    UserLogout = 2,
    /// <summary>A member was invited to a tenant.</summary>
    MemberInvited = 10,
    /// <summary>A member accepted an invitation.</summary>
    MemberInvitationAccepted = 11,
    /// <summary>A member invitation was revoked.</summary>
    MemberInvitationRevoked = 12,
    /// <summary>A member was removed from a tenant.</summary>
    MemberRemoved = 13,
    /// <summary>A member's role was changed.</summary>
    MemberRoleChanged = 14,
    /// <summary>A machine was registered.</summary>
    MachineRegistered = 20,
    /// <summary>A machine was deleted.</summary>
    MachineDeleted = 21,
    /// <summary>A machine's metadata was updated.</summary>
    MachineUpdated = 22,
    /// <summary>A tenant was created.</summary>
    TenantCreated = 30,
    /// <summary>Tenant settings were changed.</summary>
    TenantSettingsChanged = 31,
    /// <summary>A subscription was upgraded.</summary>
    SubscriptionUpgraded = 40,
    /// <summary>A subscription was downgraded.</summary>
    SubscriptionDowngraded = 41,
    /// <summary>A subscription was canceled.</summary>
    SubscriptionCanceled = 42,
    /// <summary>A subscription cancellation was requested.</summary>
    SubscriptionCancelRequested = 43,
    /// <summary>A subscription downgrade was requested.</summary>
    SubscriptionDowngradeRequested = 44,
    /// <summary>A registration token was created.</summary>
    RegistrationTokenCreated = 50,
    /// <summary>A registration token was revoked.</summary>
    RegistrationTokenRevoked = 51,
    /// <summary>A data export was requested.</summary>
    DataExportRequested = 60,
    /// <summary>A signing key was registered for remote commands.</summary>
    SigningKeyRegistered = 70,
    /// <summary>A signing key was revoked.</summary>
    SigningKeyRevoked = 71,
    /// <summary>A remote command was sent to a machine.</summary>
    RemoteCommandSent = 80,
    /// <summary>A remote command was executed by the agent.</summary>
    RemoteCommandExecuted = 81,
    /// <summary>A remote command execution failed.</summary>
    RemoteCommandFailed = 82,
    /// <summary>A signing key was authorized for a machine.</summary>
    MachineKeyAuthorized = 90,
    /// <summary>A signing key authorization was revoked for a machine.</summary>
    MachineKeyRevoked = 91,
    /// <summary>An alert rule was created.</summary>
    AlertRuleCreated = 100,
    /// <summary>An alert rule was updated.</summary>
    AlertRuleUpdated = 101,
    /// <summary>An alert rule was deleted.</summary>
    AlertRuleDeleted = 102,
    /// <summary>A webhook endpoint was created.</summary>
    WebhookCreated = 110,
    /// <summary>A webhook endpoint was updated.</summary>
    WebhookUpdated = 111,
    /// <summary>A webhook endpoint was deleted.</summary>
    WebhookDeleted = 112,
    /// <summary>A webhook endpoint secret was rotated.</summary>
    WebhookSecretRotated = 113,
    /// <summary>An alert event was acknowledged.</summary>
    AlertEventAcknowledged = 120,
}
