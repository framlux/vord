// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for registration-token-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class RegistrationTokenRepositoryTests
{
    /// <summary>
    /// Builds a <see cref="RegistrationToken"/> with sensible defaults for testing.
    /// </summary>
    private static RegistrationToken BuildRegistrationToken(int tenantId, int createdByUserId, string? tokenHash = null, string? name = null)
    {
        return new RegistrationToken
        {
            TenantId = tenantId,
            TokenHash = tokenHash ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Name = name ?? $"Test Token {Guid.NewGuid():N}",
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
    }

    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Many registration token tests require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    private static IRegistrationTokenRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    // ========== GetTokenByHashAsync tests ==========

    [Test]
    public async Task GetTokenByHashAsync_ExistingToken_ReturnsToken()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string knownHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        RegistrationToken token = BuildRegistrationToken(tenantId, userId, tokenHash: knownHash);
        await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationToken? result = await repo.GetTokenByHashAsync(knownHash);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.TokenHash).IsEqualTo(knownHash);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.CreatedByUserId).IsEqualTo(userId);
        await Assert.That(result.IsRevoked).IsFalse();
    }

    [Test]
    public async Task GetTokenByHashAsync_RoundTripsExpiresAtColumn()
    {
        // Regression for the ExpiresAt migration: the column must persist and round-trip.
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string knownHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        DateTimeOffset expiresAt = new(2026, 06, 22, 9, 30, 0, TimeSpan.Zero);
        RegistrationToken token = BuildRegistrationToken(tenantId, userId, tokenHash: knownHash);
        token.ExpiresAt = expiresAt;
        await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationToken? result = await repo.GetTokenByHashAsync(knownHash);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ExpiresAt).IsEqualTo(expiresAt);
    }

    [Test]
    public async Task GetTokenByHashAsync_NonExistentHash_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        RegistrationToken? result = await repo.GetTokenByHashAsync("0000000000000000000000000000000000000000000000000000000000000000");

        await Assert.That(result).IsNull();
    }

    // ========== CreateRegistrationTokenAsync tests ==========

    [Test]
    public async Task CreateRegistrationTokenAsync_ValidToken_ReturnsTokenWithAssignedId()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken token = BuildRegistrationToken(tenantId, userId, name: "My Server Token");

        RegistrationToken result = await repo.CreateRegistrationTokenAsync(token);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0L);
        await Assert.That(result.Name).IsEqualTo("My Server Token");
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.CreatedByUserId).IsEqualTo(userId);
        await Assert.That(result.IsRevoked).IsFalse();
        await Assert.That(result.RevokedAt).IsNull();
    }

    [Test]
    public async Task CreateRegistrationTokenAsync_NullToken_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.CreateRegistrationTokenAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== RevokeRegistrationTokenAsync tests ==========

    [Test]
    public async Task RevokeRegistrationTokenAsync_ActiveToken_ReturnsOneAndSetsRevokedFields()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken token = BuildRegistrationToken(tenantId, userId);
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, tenantId);

        await Assert.That(updated).IsEqualTo(1);

        // Verify the token was actually revoked in the database
        RegistrationToken? revoked = await repo.GetTokenByHashAsync(token.TokenHash);
        await Assert.That(revoked).IsNotNull();
        await Assert.That(revoked!.IsRevoked).IsTrue();
        await Assert.That(revoked.RevokedAt).IsNotNull();
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_AlreadyRevokedToken_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken token = BuildRegistrationToken(tenantId, userId);
        token.IsRevoked = true;
        token.RevokedAt = DateTimeOffset.UtcNow.AddHours(-1);
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, tenantId);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_WrongTenantId_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken token = BuildRegistrationToken(tenantId, userId);
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        int wrongTenantId = tenantId + 999;
        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, wrongTenantId);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_NonExistentTokenId_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int updated = await repo.RevokeRegistrationTokenAsync(99999L, 1);

        await Assert.That(updated).IsEqualTo(0);
    }

    // ========== GetRegistrationTokensForTenantAsync tests ==========

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_MultipleTokens_ReturnsOrderedByCreatedAtDescending()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken oldest = BuildRegistrationToken(tenantId, userId, name: "Oldest");
        oldest.CreatedAt = DateTimeOffset.UtcNow.AddHours(-3);
        await dbFactory.Context.InsertWithInt64IdentityAsync(oldest);

        RegistrationToken middle = BuildRegistrationToken(tenantId, userId, name: "Middle");
        middle.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await dbFactory.Context.InsertWithInt64IdentityAsync(middle);

        RegistrationToken newest = BuildRegistrationToken(tenantId, userId, name: "Newest");
        newest.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await dbFactory.Context.InsertWithInt64IdentityAsync(newest);

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(tenantId, skip: 0, take: 10);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Name).IsEqualTo("Newest");
        await Assert.That(result[1].Name).IsEqualTo("Middle");
        await Assert.That(result[2].Name).IsEqualTo("Oldest");
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_WithPagination_RespectsSkipAndTake()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Insert five tokens with distinct CreatedAt values
        for (int i = 0; i < 5; i++)
        {
            RegistrationToken token = BuildRegistrationToken(tenantId, userId, name: $"Token-{i}");
            token.CreatedAt = DateTimeOffset.UtcNow.AddHours(-5 + i);
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        // Skip the first 2 (newest), take 2 from the middle
        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(tenantId, skip: 2, take: 2);

        await Assert.That(result.Count).IsEqualTo(2);
        // Ordered desc: Token-4, Token-3, Token-2, Token-1, Token-0
        // Skip 2 gives: Token-2, Token-1
        await Assert.That(result[0].Name).IsEqualTo("Token-2");
        await Assert.That(result[1].Name).IsEqualTo("Token-1");
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        RegistrationToken token = BuildRegistrationToken(tenantId, userId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        int otherTenantId = tenantId + 999;
        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(otherTenantId, skip: 0, take: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_NoTokens_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(1, skip: 0, take: 10);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== CountRegistrationTokensForTenantAsync tests ==========

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_MultipleTokens_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        for (int i = 0; i < 4; i++)
        {
            RegistrationToken token = BuildRegistrationToken(tenantId, userId);
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        int count = await repo.CountRegistrationTokensForTenantAsync(tenantId);

        await Assert.That(count).IsEqualTo(4);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_EmptyTenant_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int count = await repo.CountRegistrationTokensForTenantAsync(99999);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_IncludesRevokedTokens()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Insert one active and one revoked token
        RegistrationToken activeToken = BuildRegistrationToken(tenantId, userId);
        await dbFactory.Context.InsertWithInt64IdentityAsync(activeToken);

        RegistrationToken revokedToken = BuildRegistrationToken(tenantId, userId);
        revokedToken.IsRevoked = true;
        revokedToken.RevokedAt = DateTimeOffset.UtcNow;
        await dbFactory.Context.InsertWithInt64IdentityAsync(revokedToken);

        int count = await repo.CountRegistrationTokensForTenantAsync(tenantId);

        // Count should include both active and revoked tokens
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_DoesNotCountOtherTenantTokens()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);
        (int userId2, int tenantId2) = await SeedUserAndTenantAsync(dbFactory);

        // Insert tokens for tenant 1
        for (int i = 0; i < 3; i++)
        {
            RegistrationToken token = BuildRegistrationToken(tenantId, userId);
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        // Insert a token for tenant 2
        RegistrationToken otherToken = BuildRegistrationToken(tenantId2, userId2);
        await dbFactory.Context.InsertWithInt64IdentityAsync(otherToken);

        int countTenant1 = await repo.CountRegistrationTokensForTenantAsync(tenantId);
        int countTenant2 = await repo.CountRegistrationTokensForTenantAsync(tenantId2);

        await Assert.That(countTenant1).IsEqualTo(3);
        await Assert.That(countTenant2).IsEqualTo(1);
    }

    // ========== CreateRegistrationTokenAsync tests (CreateRepo-helper variants) ==========

    [Test]
    public async Task CreateRegistrationTokenAsync_NullToken_ExplicitCancellationToken_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        await Assert.That(async () => await repo.CreateRegistrationTokenAsync(null!, CancellationToken.None))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CreateRegistrationTokenAsync_ValidToken_SetsGeneratedId()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "abc123",
            Name = "Test Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };

        RegistrationToken result = await repo.CreateRegistrationTokenAsync(token, CancellationToken.None);

        await Assert.That(result.Id).IsGreaterThan(0);
    }

    [Test]
    public async Task CreateRegistrationTokenAsync_ValidToken_PersistsInDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "persist-hash",
            Name = "Persist Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };

        await repo.CreateRegistrationTokenAsync(token, CancellationToken.None);

        RegistrationToken? dbToken = await dbFactory.Context.RegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == "persist-hash");
        await Assert.That(dbToken).IsNotNull();
        await Assert.That(dbToken!.Name).IsEqualTo("Persist Token");
        await Assert.That(dbToken.TenantId).IsEqualTo(1);
    }

    // ========== RevokeRegistrationTokenAsync tests (CreateRepo-helper variants) ==========

    [Test]
    public async Task RevokeRegistrationTokenAsync_TokenNotFound_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int updated = await repo.RevokeRegistrationTokenAsync(999, 1, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_WrongTenant_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 2,
            TokenHash = "wrong-tenant-hash",
            Name = "Wrong Tenant",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, 1, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_AlreadyRevoked_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "already-revoked-hash",
            Name = "Already Revoked",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = true,
            RevokedAt = DateTimeOffset.UtcNow,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, 1, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task RevokeRegistrationTokenAsync_ValidToken_ReturnsOneAndUpdatesDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "valid-revoke-hash",
            Name = "Valid Revoke",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int updated = await repo.RevokeRegistrationTokenAsync(token.Id, 1, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(1);

        RegistrationToken? revoked = await dbFactory.Context.RegistrationTokens
            .FirstOrDefaultAsync(t => t.Id == token.Id);
        await Assert.That(revoked!.IsRevoked).IsTrue();
        await Assert.That(revoked.RevokedAt.HasValue).IsTrue();
    }

    // ========== GetRegistrationTokensForTenantAsync tests (CreateRepo-helper variants) ==========

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_NoTokens_ExplicitCancellationToken_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(1, 0, 25, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_WithTokens_ReturnsOrderedByCreatedAtDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 3; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"order-hash-{i}",
                Name = $"Token {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(1, 0, 10, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Name).IsEqualTo("Token 0");
        await Assert.That(result[2].Name).IsEqualTo("Token 2");
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_RespectsSkipAndTake()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"skip-hash-{i}",
                Name = $"Token {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(1, 2, 2, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("Token 2");
        await Assert.That(result[1].Name).IsEqualTo("Token 3");
    }

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_OnlyReturnsTenantTokens()
    {
        using TestDatabaseFactory dbFactory = new();

        RegistrationToken tenant1Token = new()
        {
            TenantId = 1, TokenHash = "t1-hash", Name = "Tenant 1 Token",
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), IsRevoked = false,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(tenant1Token);

        RegistrationToken tenant2Token = new()
        {
            TenantId = 2, TokenHash = "t2-hash", Name = "Tenant 2 Token",
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), IsRevoked = false,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(tenant2Token);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        List<RegistrationToken> result = await repo.GetRegistrationTokensForTenantAsync(1, 0, 25, CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("Tenant 1 Token");
    }

    // ========== CountRegistrationTokensForTenantAsync tests (CreateRepo-helper variants) ==========

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_NoTokens_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountRegistrationTokensForTenantAsync(1, CancellationToken.None);

        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_WithTokens_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 3; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"count-hash-{i}",
                Name = $"Count Token {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountRegistrationTokensForTenantAsync(1, CancellationToken.None);

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_OnlyCountsTenantTokens()
    {
        using TestDatabaseFactory dbFactory = new();

        for (int i = 0; i < 2; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1, TokenHash = $"count-t1-{i}", Name = $"T1 Count {i}",
                CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        RegistrationToken otherToken = new()
        {
            TenantId = 2, TokenHash = "count-t2", Name = "T2 Count",
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), IsRevoked = false,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(otherToken);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountRegistrationTokensForTenantAsync(1, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountRegistrationTokensForTenantAsync_IncludesRevokedTokens_FixedTenantId()
    {
        using TestDatabaseFactory dbFactory = new();

        RegistrationToken active = new()
        {
            TenantId = 1, TokenHash = "active-count", Name = "Active",
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), IsRevoked = false,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(active);

        RegistrationToken revoked = new()
        {
            TenantId = 1, TokenHash = "revoked-count", Name = "Revoked",
            CreatedByUserId = 1, CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = true, RevokedAt = DateTimeOffset.UtcNow,
        };
        await dbFactory.Context.InsertWithInt64IdentityAsync(revoked);

        IRegistrationTokenRepository repo = CreateRepo(dbFactory);

        int count = await repo.CountRegistrationTokensForTenantAsync(1, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }
}
