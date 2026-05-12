// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Test factory that forces Billing:Enabled to false, overriding the environment variable
/// set by FunctionalTestFactory. Used to verify billing endpoints return 404 when billing
/// is disabled.
/// </summary>
public sealed class BillingDisabledTestFactory : FunctionalTestFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Override the Billing:Enabled env var set by the base class.
        // AddInMemoryCollection added last takes precedence.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Billing:Enabled"] = "false"
            });
        });
    }
}
