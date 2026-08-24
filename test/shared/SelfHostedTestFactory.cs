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
    /// <remarks>
    /// Clearing both transports reproduces the operator who has configured no relay: the Resend key
    /// is meaningless outside the hosted deployment, and an absent SMTP host is what selects the
    /// no-op email service.
    /// </remarks>
    protected override IReadOnlyDictionary<string, string?> StartupEnvironment { get; } =
        new Dictionary<string, string?>
        {
            ["Deployment__SelfHosted"] = "true",
            ["Email__FromEmail"] = "Framlux Vord <invitations@test.invalid>",
            ["Email__Resend__ApiKey"] = null,
            ["Email__Smtp__Host"] = null,
        };

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // The startup environment above steers the composition root; this collection keeps the
        // options that bind later in agreement with it. It is per-host and is added after the base
        // factory's sources, so it wins on every key it names.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:SelfHosted"] = "true",
                ["Email:Resend:ApiKey"] = string.Empty,
                ["Email:Smtp:Host"] = string.Empty
            });
        });
    }
}
