// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Builds <see cref="IQuotaPauseService"/> stand-ins for tests of the surfaces that show a machine's
/// paused state, so each test states the cutoff it is about rather than seeding a tenant over its limit.
/// </summary>
public static class TestQuotaPause
{
    /// <summary>
    /// A pause service under which no machine is paused.
    /// </summary>
    /// <returns>The service.</returns>
    public static IQuotaPauseService Nothing()
    {
        return With(QuotaCutoff.None, 0);
    }

    /// <summary>
    /// A pause service that answers with the given cutoff and paused count.
    /// </summary>
    /// <param name="cutoff">The cutoff every tenant is given.</param>
    /// <param name="pausedCount">The paused count every tenant is given.</param>
    /// <returns>The service.</returns>
    public static IQuotaPauseService With(QuotaCutoff cutoff, int pausedCount)
    {
        IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
        quotaPause.GetCutoffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(cutoff);
        quotaPause.GetPausedCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(pausedCount);

        return quotaPause;
    }

    /// <summary>
    /// A pause service whose every decision is unavailable, as when the database cannot be reached.
    /// </summary>
    /// <returns>The service.</returns>
    public static IQuotaPauseService Unavailable()
    {
        IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
        quotaPause.GetCutoffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<QuotaCutoff>>(call => throw new QuotaCheckUnavailableException(call.ArgAt<int>(0), new InvalidOperationException("database unavailable")));
        quotaPause.GetPausedCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(call => throw new QuotaCheckUnavailableException(call.ArgAt<int>(0), new InvalidOperationException("database unavailable")));

        return quotaPause;
    }
}
