// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Xml.Linq;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Persists the ASP.NET Core Data Protection key ring in Postgres, shared by api-server and
/// services-worker replicas, instead of Redis — a Redis flush must never be able to destroy
/// the ring and the tenant OIDC secrets encrypted under it.
/// </summary>
public sealed class PostgresXmlRepository : IXmlRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Creates a new instance of the <see cref="PostgresXmlRepository"/> class.
    /// </summary>
    /// <param name="scopeFactory">
    /// Factory used to create a DI scope per call. <see cref="IXmlRepository"/> is consumed as a
    /// singleton by the Data Protection key manager, while <c>DatabaseContext</c> and its
    /// repositories are scoped — a fresh scope is required for every call.
    /// </param>
    public PostgresXmlRepository(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IDataProtectionKeyRepository repository = scope.ServiceProvider.GetRequiredService<IDataProtectionKeyRepository>();

        // IXmlRepository is a synchronous contract imposed by ASP.NET Core's key management
        // subsystem, so the async repository call is blocked on here. This only runs at process
        // startup (key manager initialization) and during key rotation (roughly every 90 days),
        // never on a per-request path, so the sync-over-async cost is negligible.
        IReadOnlyList<DataProtectionKey> keys = repository.GetAllAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        List<XElement> elements = new(keys.Count);
        foreach (DataProtectionKey key in keys)
        {
            elements.Add(XElement.Parse(key.Xml));
        }

        return elements;
    }

    /// <inheritdoc/>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        using IServiceScope scope = _scopeFactory.CreateScope();
        IDataProtectionKeyRepository repository = scope.ServiceProvider.GetRequiredService<IDataProtectionKeyRepository>();

        // See the comment in GetAllElements: IXmlRepository is a synchronous contract, and this
        // runs only at startup or key-rotation time, never per-request.
        repository.InsertAsync(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
