// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests for <see cref="DatabaseRepository"/> registration token repository methods.
/// </summary>
public class RegistrationTokenRepositoryTests
{
    private static IRegistrationTokenRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    // ========== CreateRegistrationTokenAsync tests ==========

    [Test]
    public async Task CreateRegistrationTokenAsync_NullToken_ThrowsArgumentNullException()
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

    // ========== RevokeRegistrationTokenAsync tests ==========

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

    // ========== GetRegistrationTokensForTenantAsync tests ==========

    [Test]
    public async Task GetRegistrationTokensForTenantAsync_NoTokens_ReturnsEmptyList()
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

    // ========== CountRegistrationTokensForTenantAsync tests ==========

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
    public async Task CountRegistrationTokensForTenantAsync_IncludesRevokedTokens()
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
