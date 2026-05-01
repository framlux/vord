// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="RegistrationTokenHandler"/>.
/// </summary>
public class RegistrationTokenHandlerTests
{
    private static RegistrationTokenHandler CreateHandler(TestDatabaseFactory dbFactory)
    {
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());

        return new RegistrationTokenHandler(repo, repo, repo);
    }

    // ========== CreateAsync null name tests ==========

    [Test]
    public async Task CreateAsync_NullName_Returns400()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<RegistrationTokenDto> result = await handler.CreateAsync(1, 1, null!, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== CreateAsync tests ==========

    [Test]
    public async Task CreateAsync_EmptyName_Returns400()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<RegistrationTokenDto> result = await handler.CreateAsync(1, 1, "", CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task CreateAsync_ValidRequest_ReturnsTokenWithPlaintext()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<RegistrationTokenDto> result = await handler.CreateAsync(1, 1, "My Token", CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Name).IsEqualTo("My Token");
        await Assert.That(result.Data!.Token).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(result.Data!.Token)).IsFalse();
        await Assert.That(result.Data!.IsRevoked).IsFalse();
    }

    [Test]
    public async Task CreateAsync_ValidRequest_InsertsTokenInDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        await handler.CreateAsync(1, 1, "DB Token", CancellationToken.None);

        List<RegistrationToken> tokens = await dbFactory.Context.RegistrationTokens.ToListAsync();
        await Assert.That(tokens.Count).IsEqualTo(1);
        await Assert.That(tokens[0].Name).IsEqualTo("DB Token");
        await Assert.That(tokens[0].TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_ValidRequest_TokenHashDiffersFromPlaintext()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<RegistrationTokenDto> result = await handler.CreateAsync(1, 1, "Hash Token", CancellationToken.None);

        RegistrationToken? dbToken = await dbFactory.Context.RegistrationTokens.FirstOrDefaultAsync();
        await Assert.That(dbToken).IsNotNull();
        await Assert.That(dbToken!.TokenHash).IsNotEqualTo(result.Data!.Token);
    }

    [Test]
    public async Task CreateAsync_ValidRequest_InsertsAuditLogEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        await handler.CreateAsync(1, 1, "Audit Token", CancellationToken.None);

        List<AuditLogEntry> auditEntries = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(auditEntries.Count).IsEqualTo(1);
        await Assert.That(auditEntries[0].TenantId).IsEqualTo(1);
    }

    // ========== RevokeAsync tests ==========

    [Test]
    public async Task RevokeAsync_TokenNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<object> result = await handler.RevokeAsync(999, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_WrongTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 2,
            TokenHash = "hash-123",
            Name = "Wrong Tenant Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<object> result = await handler.RevokeAsync(token.Id, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_AlreadyRevoked_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "hash-revoked",
            Name = "Revoked Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = true,
            RevokedAt = DateTimeOffset.UtcNow,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<object> result = await handler.RevokeAsync(token.Id, 1, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_ValidToken_SetsRevokedFlag()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "hash-valid",
            Name = "Valid Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<object> result = await handler.RevokeAsync(token.Id, 1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        RegistrationToken? revoked = await dbFactory.Context.RegistrationTokens.FirstOrDefaultAsync(t => t.Id == token.Id);
        await Assert.That(revoked!.IsRevoked).IsTrue();
        await Assert.That(revoked.RevokedAt.HasValue).IsTrue();
    }

    [Test]
    public async Task RevokeAsync_ValidToken_InsertsAuditLogEntry()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationToken token = new()
        {
            TenantId = 1,
            TokenHash = "hash-audit-revoke",
            Name = "Audit Revoke Token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRevoked = false,
        };
        token.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(token);

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        await handler.RevokeAsync(token.Id, 1, 1, CancellationToken.None);

        List<AuditLogEntry> auditEntries = await dbFactory.Context.AuditLog.ToListAsync();
        await Assert.That(auditEntries.Count).IsEqualTo(1);
    }

    // ========== ListAsync tests ==========

    [Test]
    public async Task ListAsync_NoTokens_ReturnsEmptyPage()
    {
        using TestDatabaseFactory dbFactory = new();
        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await handler.ListAsync(1, 1, 25, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_WithTokens_ReturnsPaginatedResults()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 5; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"hash-{i}",
                Name = $"Token {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await handler.ListAsync(1, 1, 2, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.TotalCount).IsEqualTo(5);
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Page).IsEqualTo(1);
        await Assert.That(result.Data!.PageSize).IsEqualTo(2);
    }

    [Test]
    public async Task ListAsync_PageBeyondResults_ReturnsEmptyItems()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 3; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"hash-beyond-{i}",
                Name = $"Token Beyond {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await handler.ListAsync(1, 5, 2, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(0);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(3);
    }

    [Test]
    public async Task ListAsync_ExactlyFillsPage_HasCorrectTotalCount()
    {
        using TestDatabaseFactory dbFactory = new();
        for (int i = 0; i < 2; i++)
        {
            RegistrationToken token = new()
            {
                TenantId = 1,
                TokenHash = $"hash-exact-{i}",
                Name = $"Token Exact {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                IsRevoked = false,
            };
            await dbFactory.Context.InsertWithInt64IdentityAsync(token);
        }

        RegistrationTokenHandler handler = CreateHandler(dbFactory);

        ServiceResult<PaginatedResponse<RegistrationTokenDto>> result = await handler.ListAsync(1, 1, 2, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Items.Count).IsEqualTo(2);
        await Assert.That(result.Data!.TotalCount).IsEqualTo(2);
        await Assert.That(result.Data!.HasNextPage).IsFalse();
        await Assert.That(result.Data!.HasPreviousPage).IsFalse();
    }
}
