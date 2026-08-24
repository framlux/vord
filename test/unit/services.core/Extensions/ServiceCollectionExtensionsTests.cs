// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Options;
using Npgsql;

namespace Framlux.FleetManagement.Test.Services.Extensions;

/// <summary>Tests connection-string construction from DatabaseOptions pool settings.</summary>
public class ServiceCollectionExtensionsTests
{
    [Test]
    public async Task BuildConnectionString_UsesConfiguredPoolSizes()
    {
        DatabaseOptions opts = new()
        {
            Hostname = "db", User = "u", Password = "p", Db = "fleet",
            MaxPoolSize = 120, MinPoolSize = 10,
        };

        string conn = ServiceCollectionExtensions.BuildConnectionString(opts, "worker");
        NpgsqlConnectionStringBuilder parsed = new(conn);

        await Assert.That(parsed.MaxPoolSize).IsEqualTo(120);
        await Assert.That(parsed.MinPoolSize).IsEqualTo(10);
    }

    [Test]
    public async Task BuildConnectionString_UsesDefaultPoolSizes()
    {
        DatabaseOptions opts = new() { Hostname = "db", User = "u", Password = "p", Db = "fleet" };

        NpgsqlConnectionStringBuilder parsed = new(ServiceCollectionExtensions.BuildConnectionString(opts, "api"));

        await Assert.That(parsed.MaxPoolSize).IsEqualTo(50);
        await Assert.That(parsed.MinPoolSize).IsEqualTo(5);
    }
}
