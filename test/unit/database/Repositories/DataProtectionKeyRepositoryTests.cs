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
/// Unit tests for <see cref="DatabaseRepository"/>'s <see cref="IDataProtectionKeyRepository"/>
/// implementation.
/// </summary>
public sealed class DataProtectionKeyRepositoryTests
{
    private static IDataProtectionKeyRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
    }

    [Test]
    public async Task GetAllAsync_EmptyTable_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataProtectionKeyRepository repo = CreateRepo(dbFactory);

        IReadOnlyList<DataProtectionKey> result = await repo.GetAllAsync(CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InsertAsync_ThenGetAllAsync_ReturnsInsertedKeyWithXmlRoundTripped()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataProtectionKeyRepository repo = CreateRepo(dbFactory);

        const string xml = "<key id=\"11111111-1111-1111-1111-111111111111\"><secret>abc123</secret></key>";
        DateTimeOffset createdAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await repo.InsertAsync(new DataProtectionKey
        {
            FriendlyName = "key-11111111-1111-1111-1111-111111111111",
            Xml = xml,
            CreatedAt = createdAt,
        }, CancellationToken.None);

        IReadOnlyList<DataProtectionKey> result = await repo.GetAllAsync(CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Xml).IsEqualTo(xml);
        await Assert.That(result[0].FriendlyName).IsEqualTo("key-11111111-1111-1111-1111-111111111111");
        await Assert.That(result[0].CreatedAt).IsEqualTo(createdAt);
    }

    [Test]
    public async Task InsertAsync_TwoKeys_GetAllAsyncReturnsBoth()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataProtectionKeyRepository repo = CreateRepo(dbFactory);

        const string xmlOne = "<key id=\"1\"><secret>one</secret></key>";
        const string xmlTwo = "<key id=\"2\"><secret>two</secret></key>";

        await repo.InsertAsync(new DataProtectionKey
        {
            FriendlyName = "key-1",
            Xml = xmlOne,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);
        await repo.InsertAsync(new DataProtectionKey
        {
            FriendlyName = "key-2",
            Xml = xmlTwo,
            CreatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        IReadOnlyList<DataProtectionKey> result = await repo.GetAllAsync(CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(k => k.Xml == xmlOne)).IsTrue();
        await Assert.That(result.Any(k => k.Xml == xmlTwo)).IsTrue();
    }

    [Test]
    public async Task InsertAsync_NullFriendlyName_IsPersisted()
    {
        // Intent: ASP.NET Core's XmlKeyManager does not always supply a friendly name; the
        // column must tolerate null rather than throwing a NOT NULL constraint violation.
        using TestDatabaseFactory dbFactory = new();
        IDataProtectionKeyRepository repo = CreateRepo(dbFactory);

        await repo.InsertAsync(new DataProtectionKey
        {
            FriendlyName = null,
            Xml = "<key/>",
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        IReadOnlyList<DataProtectionKey> result = await repo.GetAllAsync(CancellationToken.None);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].FriendlyName).IsNull();
    }
}
