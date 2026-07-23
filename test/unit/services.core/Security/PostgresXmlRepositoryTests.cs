// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Xml.Linq;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Security;

/// <summary>
/// Unit tests for <see cref="PostgresXmlRepository"/>.
/// </summary>
public sealed class PostgresXmlRepositoryTests
{
    private static (PostgresXmlRepository Repository, IDataProtectionKeyRepository KeyRepo) CreateRepository()
    {
        IDataProtectionKeyRepository keyRepo = Substitute.For<IDataProtectionKeyRepository>();

        ServiceCollection services = new();
        services.AddSingleton(keyRepo);
        ServiceProvider provider = services.BuildServiceProvider();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return (new PostgresXmlRepository(scopeFactory), keyRepo);
    }

    [Test]
    public async Task GetAllElements_EmptyRepository_ReturnsEmptyCollection()
    {
        (PostgresXmlRepository repository, IDataProtectionKeyRepository keyRepo) = CreateRepository();
        keyRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DataProtectionKey>>([]));

        IReadOnlyCollection<XElement> result = repository.GetAllElements();

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StoreElement_ThenGetAllElements_RoundTripsXElement()
    {
        (PostgresXmlRepository repository, IDataProtectionKeyRepository keyRepo) = CreateRepository();

        XElement stored = new("key", new XAttribute("id", "11111111-1111-1111-1111-111111111111"));
        DataProtectionKey? capturedRow = null;
        keyRepo.InsertAsync(Arg.Do<DataProtectionKey>(row => capturedRow = row), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        repository.StoreElement(stored, "key-11111111-1111-1111-1111-111111111111");

        await Assert.That(capturedRow).IsNotNull();
        await Assert.That(capturedRow!.FriendlyName).IsEqualTo("key-11111111-1111-1111-1111-111111111111");
        await Assert.That(capturedRow.Xml).IsEqualTo(stored.ToString(SaveOptions.DisableFormatting));

        keyRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DataProtectionKey>>([
                new DataProtectionKey
                {
                    Id = 1,
                    FriendlyName = capturedRow.FriendlyName,
                    Xml = capturedRow.Xml,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ]));

        IReadOnlyCollection<XElement> result = repository.GetAllElements();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(XNode.DeepEquals(result.Single(), stored)).IsTrue();
    }

    [Test]
    public async Task Constructor_NullScopeFactory_Throws()
    {
        await Assert.That(() => new PostgresXmlRepository(null!)).Throws<ArgumentNullException>();
    }
}
