// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Puts <see cref="TestClientCertificateMiddleware"/> at the very front of the test host's
/// pipeline, which is where Kestrel would have surfaced a real TLS client certificate.
/// </summary>
public sealed class TestClientCertificateStartupFilter : IStartupFilter
{
    /// <inheritdoc/>
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<TestClientCertificateMiddleware>();
            next(app);
        };
    }
}
