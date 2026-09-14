// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts machine registration attempts by outcome.
/// </summary>
/// <remarks>
/// Registration is the first thing a new customer does and the first thing that can fail without
/// anybody hearing about it. Attribution is frequently the unknown bucket by necessity: a bad token
/// is precisely the case where no tenant has been identified yet.
/// </remarks>
public sealed class RegistrationMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _failures;

    /// <summary>
    /// Creates the registration instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public RegistrationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _attempts = meter.CreateCounter<long>(
            "vord.registration.attempts",
            description: "Machine registration attempts by outcome.");

        _failures = meter.CreateCounter<long>(
            "vord.registration.failures",
            description: "Registration failures attributed to the tenant whose token was used.");
    }

    /// <summary>
    /// Records one registration attempt.
    /// </summary>
    /// <param name="outcome">How it ended.</param>
    /// <param name="tenantId">The internal tenant id once the token has resolved one, otherwise
    /// null. Used only on failures.</param>
    public void RecordAttempt(RegistrationOutcome outcome, int? tenantId)
    {
        KeyValuePair<string, object?> outcomeTag = new("outcome", MetricTag.From(outcome));

        _attempts.Add(1, outcomeTag);

        if (outcome == RegistrationOutcome.Registered)
        {
            return;
        }

        string tenant = tenantId is null
            ? MetricTag.Unknown
            : tenantId.Value.ToString(CultureInfo.InvariantCulture);

        _failures.Add(1, new KeyValuePair<string, object?>("tenant", tenant), outcomeTag);
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (RegistrationOutcome outcome in Enum.GetValues<RegistrationOutcome>())
        {
            _attempts.Add(0, new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
        }
    }
}
