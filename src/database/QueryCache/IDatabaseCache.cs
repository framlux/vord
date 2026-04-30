// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Cache;

/// <summary>
/// The cache'd interface for interacting with the database
/// </summary>
public interface IDatabaseCache
{
    /// <summary>
    /// Begins a database transaction on the shared scoped connection.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a transaction that must be committed or disposed</returns>
    Task<IDatabaseTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts an audit log entry into the database.
    /// </summary>
    /// <param name="entry">The audit log entry to insert</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task InsertAuditLogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the tenants and roles for the specified user
    /// </summary>
    /// <param name="userUniqueId">The unique ID of the user</param>
    /// <param name="cancellationToken">Token used to cancel the async task</param>
    /// <returns>Returns the list of tenants the user has access to</returns>
    Task<IEnumerable<UserTenantRole>> GetTenantsForUserAsync(string userUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a user by their external ID
    /// </summary>
    /// <param name="externalId">The ID from the IdP</param>
    /// <param name="cancellationToken">Token for cancelling the async task</param>
    /// <returns>Returns the User Account if found; otherwise, returns null</returns>
    Task<UserAccount?> GetUserByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new user account in the database
    /// </summary>
    /// <param name="user">The account to create</param>
    /// <param name="cancellationToken">Token for cancelling the async task</param>
    /// <returns>Returns the account that was created</returns>
    Task<UserAccount> CreateUserAccountAsync(UserAccount user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a user's email address in the database
    /// </summary>
    /// <param name="userId">The ID of the user to update</param>
    /// <param name="newEmail">The new email address</param>
    /// <param name="cancellationToken">Token for cancelling the async task</param>
    /// <returns>Returns an awaitable Task</returns>
    Task UpdateUserEmailAsync(int userId, string newEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a user's authentication provider in the database
    /// </summary>
    /// <param name="userId">The ID of the user to update</param>
    /// <param name="authProvider">The authentication provider type</param>
    /// <param name="cancellationToken">Token for cancelling the async task</param>
    /// <returns>Returns an awaitable Task</returns>
    Task UpdateUserAuthProviderAsync(int userId, AuthProviderType authProvider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its external ID
    /// </summary>
    /// <param name="externalId">The external ID to query by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns a Tenant object if one is found; otherwise, returns null</returns>
    Task<Tenant?> GetTenantByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its unique identifier
    /// </summary>
    /// <param name="tenantId">The internal unique ID of the tenant</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns a Tenant object is one is found; otherwise, returns null</returns>
    Task<Tenant?> GetTenantByIdAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its name
    /// </summary>
    /// <param name="name">The case-insensitive name to query by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns a Tenant object if one is found; otherwise, returns null</returns>
    Task<Tenant?> GetTenantByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tenant in the database
    /// </summary>
    /// <param name="tenant">The tenant to create</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the created Tenant object with ID</returns>
    Task<Tenant> CreateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tenant subscription in the database using the shared scoped connection.
    /// </summary>
    /// <param name="subscription">The subscription to create</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the created subscription with ID</returns>
    Task<TenantSubscription> CreateTenantSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an active (non-deleted) machine exists based on the serial number, system ID, or asset tag number
    /// </summary>
    /// <param name="serialNumber">The serial number of the machine</param>
    /// <param name="systemId">The system ID of the machine</param>
    /// <param name="assetTag">The asset tag of the machine</param>
    /// <param name="tenantId">The tenant ID to scope the search to</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns true if a machine exists identified by one of the IDs; otherwise, returns false</returns>
    Task<bool> DoesMachineExistAsync(string serialNumber, string systemId, string assetTag, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new Machine with a hashed API key and returns the plaintext key
    /// </summary>
    /// <param name="machine">The machine to create</param>
    /// <param name="machineLimit">Optional machine limit to enforce atomically. If set, creation fails when the tenant is at or over the limit.</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns a tuple of the created machine (with ID) and plaintext API key, or (null, null) if machine limit exceeded</returns>
    Task<(Machine? machine, string? plaintextApiKey)> CreateMachineWithKeyAsync(Machine machine, int? machineLimit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a new API key for an existing machine, replacing the old one.
    /// Updates ApiKeyHash and resets KeyDeliveredAt to null atomically.
    /// </summary>
    /// <param name="machineId">The ID of the machine to re-key</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the new plaintext API key, or null if the machine was not found</returns>
    Task<string?> ReissueApiKeyAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an approved machine exists, and is active, with the given ID within a tenant
    /// </summary>
    /// <param name="machineId">The ID to use as a lookup</param>
    /// <param name="tenantId">The tenant ID to filter by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the machine entry if the machine exists and is active; otherwise, returns null</returns>
    Task<Machine?> GetMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if any user accounts exist in the database
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns true if any user accounts exist; otherwise, false</returns>
    Task<bool> DoAnyUsersExistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a machine by its API key
    /// </summary>
    /// <param name="apiKey">The API key of the machine</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the machine if found; otherwise, returns null</returns>
    Task<Machine?> GetMachineByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a user-tenant role assignment in the database
    /// </summary>
    /// <param name="role">The role assignment to create</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns an awaitable Task</returns>
    Task CreateUserTenantRoleAsync(UserTenantRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the OIDC configuration for a tenant
    /// </summary>
    /// <param name="tenantId">The ID of the tenant</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the OIDC configuration if found and enabled; otherwise, returns null</returns>
    Task<TenantOidcConfiguration?> GetTenantOidcConfigurationAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the OIDC configuration for a tenant by email domain
    /// </summary>
    /// <param name="emailDomain">The email domain to look up (e.g. "example.com")</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the OIDC configuration if found and enabled; otherwise, returns null</returns>
    Task<TenantOidcConfiguration?> GetTenantOidcConfigurationByEmailDomainAsync(string emailDomain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tenant invitation in the database
    /// </summary>
    /// <param name="invitation">The invitation to create</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the created invitation with ID</returns>
    Task<TenantInvitation> CreateInvitationAsync(TenantInvitation invitation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant invitation by its unique token
    /// </summary>
    /// <param name="token">The invitation token</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the invitation if found; otherwise, returns null</returns>
    Task<TenantInvitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all invitations for a tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID to filter by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns a list of invitations for the tenant</returns>
    Task<IEnumerable<TenantInvitation>> GetInvitationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a pending invitation for a specific email and tenant
    /// </summary>
    /// <param name="email">The email address to check</param>
    /// <param name="tenantId">The tenant ID to filter by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the pending invitation if found; otherwise, returns null</returns>
    Task<TenantInvitation?> GetPendingInvitationByEmailAndTenantAsync(string email, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status of a tenant invitation
    /// </summary>
    /// <param name="invitationId">The ID of the invitation to update</param>
    /// <param name="status">The new status</param>
    /// <param name="acceptedByUserId">The user who accepted, if applicable</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns an awaitable Task</returns>
    Task UpdateInvitationStatusAsync(int invitationId, Enums.InvitationStatus status, int? acceptedByUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a tenant invitation
    /// </summary>
    /// <param name="invitationId">The ID of the invitation to revoke</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns an awaitable Task</returns>
    Task RevokeInvitationAsync(int invitationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user account by email address
    /// </summary>
    /// <param name="email">The email address to look up</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the user account if found; otherwise, returns null</returns>
    Task<UserAccount?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active user-tenant roles for a specific tenant
    /// </summary>
    /// <param name="tenantId">The tenant ID to filter by</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns the list of active user-tenant roles with associated user data</returns>
    Task<IEnumerable<UserTenantRole>> GetMembersForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a subscription after a checkout completes.
    /// </summary>
    Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the current period end of a subscription.
    /// </summary>
    Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a subscription to the Free tier after cancellation.
    /// </summary>
    /// <param name="tenantId">The tenant to revert.</param>
    /// <param name="machineLimit">The free tier machine limit.</param>
    /// <param name="retentionDays">The free tier retention period in days.</param>
    /// <param name="alertRuleLimit">The free tier alert rule limit.</param>
    /// <param name="webhookLimit">The free tier webhook endpoint limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevertSubscriptionToFreeAsync(int tenantId, int machineLimit, int retentionDays, int alertRuleLimit, int webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a subscription status to PastDue after a payment failure.
    /// </summary>
    Task SetSubscriptionPastDueAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<Database.Models.TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a subscription status to Active after a successful payment recovery.
    /// </summary>
    Task SetSubscriptionActiveAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downgrades a subscription from Team to Pro tier.
    /// </summary>
    Task DowngradeSubscriptionToProAsync(int tenantId, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the cancel-at-period-end flag and pending action on a subscription.
    /// </summary>
    Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, PendingSubscriptionAction pendingAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a subscription by setting its status to Canceled and clearing pending state.
    /// </summary>
    Task DeactivateSubscriptionAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions with pending cancellations that are still active.
    /// </summary>
    Task<List<Database.Models.TenantSubscription>> GetPendingCancellationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions where the tier is not Free (i.e., paid subscriptions that have a Stripe counterpart).
    /// </summary>
    Task<List<Database.Models.TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken = default);

    // --- Signing Keys ---

    /// <summary>
    /// Creates a new user signing key in the database.
    /// </summary>
    /// <param name="key">The signing key to create</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the created signing key with ID</returns>
    Task<UserSigningKey> CreateSigningKeyAsync(UserSigningKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all signing keys for a user within a tenant.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of signing keys ordered by creation date descending</returns>
    Task<List<UserSigningKey>> GetSigningKeysForUserAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of active (non-revoked) signing keys for a user within a tenant.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the count of active signing keys</returns>
    Task<int> GetActiveSigningKeyCountAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a signing key by its ID.
    /// </summary>
    /// <param name="keyId">The signing key ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the signing key if found; otherwise, null</returns>
    Task<UserSigningKey?> GetSigningKeyByIdAsync(int keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a signing key.
    /// </summary>
    /// <param name="keyId">The signing key ID to revoke</param>
    /// <param name="revokedByUserId">The user performing the revocation</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task RevokeSigningKeyAsync(int keyId, int revokedByUserId, CancellationToken cancellationToken = default);

    // --- Machine Authorized Keys ---

    /// <summary>
    /// Creates a new machine authorization record in the database.
    /// </summary>
    /// <param name="key">The machine authorization to create</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the created authorization record with generated ID</returns>
    Task<MachineAuthorizedKey> CreateMachineAuthorizationAsync(MachineAuthorizedKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all authorized key records for a machine, including active and revoked.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of authorization records ordered by AuthorizedAt descending</returns>
    Task<List<MachineAuthorizedKey>> GetAuthorizedKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active signing keys that are authorized for a specific machine.
    /// Only returns keys where both the authorization and the signing key are not revoked.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of active signing keys authorized for the machine</returns>
    Task<List<UserSigningKey>> GetActiveSigningKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a signing key has an active authorization for a machine.
    /// </summary>
    /// <param name="signingKeyId">The signing key ID</param>
    /// <param name="machineId">The machine ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns true if an active authorization exists; otherwise, false</returns>
    Task<bool> IsKeyAuthorizedForMachineAsync(int signingKeyId, long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a machine authorization by setting RevokedAt and RevokedByUserId on the active record.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="signingKeyId">The signing key ID</param>
    /// <param name="revokedByUserId">The user performing the revocation</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task RevokeMachineAuthorizationAsync(long machineId, int signingKeyId, int revokedByUserId, CancellationToken cancellationToken = default);

    // --- Remote Commands ---

    /// <summary>
    /// Creates a new remote command in the database.
    /// </summary>
    /// <param name="command">The remote command to create</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the created remote command with ID</returns>
    Task<RemoteCommand> CreateRemoteCommandAsync(RemoteCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pending commands for a specific machine that have not expired.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="tenantId">The tenant ID for isolation</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of pending commands</returns>
    Task<List<RemoteCommand>> GetPendingCommandsForMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a remote command by its client-generated command ID.
    /// </summary>
    /// <param name="commandId">The command UUID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the remote command if found; otherwise, null</returns>
    Task<RemoteCommand?> GetRemoteCommandByCommandIdAsync(string commandId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a remote command by its database ID with related entities.
    /// </summary>
    /// <param name="id">The database ID</param>
    /// <param name="tenantId">The tenant ID for authorization</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the remote command if found; otherwise, null</returns>
    Task<RemoteCommand?> GetRemoteCommandByIdAsync(long id, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets command history for a machine with pagination.
    /// </summary>
    /// <param name="machineId">The machine ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="page">The page number (1-based)</param>
    /// <param name="pageSize">The number of items per page</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a paginated list of remote commands</returns>
    Task<List<RemoteCommand>> GetCommandsForMachineAsync(long machineId, int tenantId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a remote command's status and result fields.
    /// </summary>
    /// <param name="commandId">The command UUID</param>
    /// <param name="machineId">The machine ID for ownership verification</param>
    /// <param name="status">The new status</param>
    /// <param name="exitCode">The exit code, if applicable</param>
    /// <param name="stdout">Standard output, if applicable</param>
    /// <param name="stderr">Standard error, if applicable</param>
    /// <param name="resultMessage">A human-readable result message</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task UpdateRemoteCommandStatusAsync(string commandId, long machineId, Enums.RemoteCommandStatus status, int? exitCode, string? stdout, string? stderr, string? resultMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a nonce has already been used by any remote command.
    /// </summary>
    /// <param name="nonce">The nonce to check</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns true if the nonce has been used; otherwise, false</returns>
    Task<bool> IsNonceUsedAsync(string nonce, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires all pending commands that have passed their expiry time.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task ExpirePendingCommandsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks commands as delivered after they have been sent to the agent via gRPC.
    /// </summary>
    /// <param name="commandIds">The command UUIDs to mark as delivered</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task MarkCommandsDeliveredAsync(IEnumerable<string> commandIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables a user-tenant role assignment (removes a member from a tenant)
    /// </summary>
    /// <param name="userId">The user ID to remove</param>
    /// <param name="tenantId">The tenant to remove the user from</param>
    /// <param name="disabledByUserId">The user performing the removal</param>
    /// <param name="cancellationToken">The token used to cancel async tasks</param>
    /// <returns>Returns true if a role was disabled; false if none found</returns>
    Task<bool> DisableUserTenantRoleAsync(int userId, int tenantId, int disabledByUserId, CancellationToken cancellationToken = default);
}
