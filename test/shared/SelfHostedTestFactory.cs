// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Test factory that runs the host as a self-hosted deployment, overriding the hosted default
/// set by <see cref="FunctionalTestFactory"/>. Used to verify that the billing surfaces are
/// absent when there is no SaaS control plane behind them.
/// </summary>
public sealed class SelfHostedTestFactory : FunctionalTestFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Per-host rather than process-global, because tests run in parallel and an environment
        // variable would race across concurrently constructed hosts. An in-memory collection
        // added last takes precedence over the environment.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:SelfHosted"] = "true"
            });
        });
    }
}
