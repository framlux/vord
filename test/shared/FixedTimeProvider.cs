// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> for tests that returns a fixed, controllable
/// instant from <see cref="GetUtcNow"/>. Lets tests assert time-dependent behavior without
/// depending on the wall clock.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedTimeProvider"/> class.
    /// </summary>
    /// <param name="now">The instant that <see cref="GetUtcNow"/> will return.</param>
    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    /// <summary>
    /// Advances the provider's current time by the supplied amount.
    /// </summary>
    /// <param name="delta">The amount of time to advance.</param>
    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
    }
}
