// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.DataExport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Test factory that registers the real <see cref="NoOpObjectStorageService"/> instead of the
/// substitute object storage used by <see cref="FunctionalTestFactory"/>. Used to verify that
/// data export endpoints refuse requests when no object storage backend is configured.
/// </summary>
public sealed class ObjectStorageDisabledTestFactory : FunctionalTestFactory
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Registered after the base class's substitute so RemoveAll clears it and the
        // endpoint's NoOpObjectStorageService type check sees the real no-op implementation.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IObjectStorageService>();
            services.AddSingleton<IObjectStorageService, NoOpObjectStorageService>();
        });
    }
}
