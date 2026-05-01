// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for invitation-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class InvitationCacheTests
{
    /// <summary>
    /// Computes a SHA-256 hash of a token for test setup (mirrors production hashing).
    /// </summary>
    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Many invitation tests require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    [Test]
    public async Task CreateInvitationAsync_ValidInvitation_ReturnsInvitationWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "newmember@example.com",
            invitedByUserId: userId);

        TenantInvitation result = await cache.CreateInvitationAsync(invitation);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.Email).IsEqualTo("newmember@example.com");
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.Status).IsEqualTo(InvitationStatus.Pending);
    }

    [Test]
    public async Task GetInvitationByTokenAsync_ExistingToken_ReturnsInvitationWithJoinedData()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string plaintextToken = "test-token-for-lookup";
        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "token-lookup@example.com",
            invitedByUserId: userId);
        invitation.TokenHash = HashToken(plaintextToken);
        invitation.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        TenantInvitation? result = await cache.GetInvitationByTokenAsync(plaintextToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(invitation.Id);
        await Assert.That(result.Email).IsEqualTo("token-lookup@example.com");
        await Assert.That(result.TokenHash).IsEqualTo(invitation.TokenHash);
        await Assert.That(result.Tenant).IsNotNull();
        await Assert.That(result.InvitedByUser).IsNotNull();
    }

    [Test]
    public async Task GetInvitationByTokenAsync_NonExistentToken_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        TenantInvitation? result = await cache.GetInvitationByTokenAsync("nonexistent-token-value");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetInvitationsForTenantAsync_MultipleInvitations_ReturnsAllOrderedByCreatedAtDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        TenantInvitation inv1 = TestDataBuilder.BuildInvitation(tenantId: tenantId, email: "first@example.com", invitedByUserId: userId);
        inv1.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await dbFactory.Context.InsertWithInt32IdentityAsync(inv1);

        TenantInvitation inv2 = TestDataBuilder.BuildInvitation(tenantId: tenantId, email: "second@example.com", invitedByUserId: userId);
        inv2.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await dbFactory.Context.InsertWithInt32IdentityAsync(inv2);

        TenantInvitation inv3 = TestDataBuilder.BuildInvitation(tenantId: tenantId, email: "third@example.com", invitedByUserId: userId);
        inv3.CreatedAt = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertWithInt32IdentityAsync(inv3);

        IEnumerable<TenantInvitation> result = await cache.GetInvitationsForTenantAsync(tenantId);

        List<TenantInvitation> resultList = result.ToList();

        await Assert.That(resultList.Count).IsEqualTo(3);
        await Assert.That(resultList[0].Email).IsEqualTo("third@example.com");
        await Assert.That(resultList[2].Email).IsEqualTo("first@example.com");
    }

    [Test]
    public async Task GetInvitationsForTenantAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        TenantInvitation invitation = TestDataBuilder.BuildInvitation(tenantId: tenantId, invitedByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        IEnumerable<TenantInvitation> result = await cache.GetInvitationsForTenantAsync(99999);

        await Assert.That(result.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingInvitationByEmailAndTenantAsync_ExistingPending_ReturnsInvitation()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "pending-check@example.com",
            status: InvitationStatus.Pending,
            invitedByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        TenantInvitation? result = await cache.GetPendingInvitationByEmailAndTenantAsync("pending-check@example.com", tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Email).IsEqualTo("pending-check@example.com");
        await Assert.That(result.Status).IsEqualTo(InvitationStatus.Pending);
    }

    [Test]
    public async Task GetPendingInvitationByEmailAndTenantAsync_AcceptedInvitation_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "accepted@example.com",
            status: InvitationStatus.Accepted,
            invitedByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        TenantInvitation? result = await cache.GetPendingInvitationByEmailAndTenantAsync("accepted@example.com", tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetPendingInvitationByEmailAndTenantAsync_NonExistentEmail_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        TenantInvitation? result = await cache.GetPendingInvitationByEmailAndTenantAsync("nobody@nowhere.com", 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task UpdateInvitationStatusAsync_AcceptInvitation_SetsStatusAndAcceptedFields()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string plaintextToken = "accept-token";
        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "accept-me@example.com",
            status: InvitationStatus.Pending,
            invitedByUserId: userId);
        invitation.TokenHash = HashToken(plaintextToken);
        invitation.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        await cache.UpdateInvitationStatusAsync(invitation.Id, InvitationStatus.Accepted, acceptedByUserId: userId);

        TenantInvitation? result = await cache.GetInvitationByTokenAsync(plaintextToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(InvitationStatus.Accepted);
        await Assert.That(result.AcceptedByUserId).IsEqualTo(userId);
        await Assert.That(result.AcceptedAt).IsNotNull();
    }

    [Test]
    public async Task UpdateInvitationStatusAsync_ExpireInvitation_SetsStatusOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string plaintextToken = "expire-token";
        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "expire-me@example.com",
            status: InvitationStatus.Pending,
            invitedByUserId: userId);
        invitation.TokenHash = HashToken(plaintextToken);
        invitation.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        await cache.UpdateInvitationStatusAsync(invitation.Id, InvitationStatus.Expired);

        TenantInvitation? result = await cache.GetInvitationByTokenAsync(plaintextToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(InvitationStatus.Expired);
        await Assert.That(result.AcceptedByUserId).IsNull();
        await Assert.That(result.AcceptedAt).IsNull();
    }

    [Test]
    public async Task RevokeInvitationAsync_PendingInvitation_SetsRevokedStatusAndTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string plaintextToken = "revoke-token";
        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "revoke-me@example.com",
            status: InvitationStatus.Pending,
            invitedByUserId: userId);
        invitation.TokenHash = HashToken(plaintextToken);
        invitation.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        await cache.RevokeInvitationAsync(invitation.Id);

        TenantInvitation? result = await cache.GetInvitationByTokenAsync(plaintextToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(InvitationStatus.Revoked);
        await Assert.That(result.RevokedAt).IsNotNull();
    }

    [Test]
    public async Task RevokeInvitationAsync_InvitationNoLongerPending_StillUpdatesStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IInvitationRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string plaintextToken = "revoke-accepted-token";
        TenantInvitation invitation = TestDataBuilder.BuildInvitation(
            tenantId: tenantId,
            email: "revoke-accepted@example.com",
            status: InvitationStatus.Accepted,
            invitedByUserId: userId);
        invitation.TokenHash = HashToken(plaintextToken);
        invitation.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(invitation);

        await cache.RevokeInvitationAsync(invitation.Id);

        TenantInvitation? result = await cache.GetInvitationByTokenAsync(plaintextToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(InvitationStatus.Revoked);
        await Assert.That(result.RevokedAt).IsNotNull();
    }
}
