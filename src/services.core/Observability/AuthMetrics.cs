// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts user logins by identity provider and outcome, with tenant SSO failures attributed.
/// </summary>
/// <remarks>
/// The provider is the enum value, never a tenant's configured provider name: a slug would grow the
/// label set with the customer count and would be caller-influenced.
///
/// Attribution is restricted to tenant SSO because that is the only provider with a tenant in
/// scope — social login is not tenant-scoped — and because a Team-tier customer locked out by their
/// own identity provider is a failure answered per-customer or not at all.
/// </remarks>
public sealed class AuthMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _logins;
    private readonly Counter<long> _ssoLoginFailures;

    /// <summary>
    /// Creates the authentication instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public AuthMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _logins = meter.CreateCounter<long>(
            "vord.auth.logins",
            description: "User login attempts by identity provider and outcome.");

        _ssoLoginFailures = meter.CreateCounter<long>(
            "vord.auth.sso_login_failures",
            description: "Tenant SSO login failures attributed to the tenant whose provider is configured.");
    }

    /// <summary>
    /// Records one login attempt.
    /// </summary>
    /// <param name="provider">The identity provider used.</param>
    /// <param name="outcome">How the attempt ended.</param>
    public void RecordLogin(AuthProviderType provider, LoginOutcome outcome)
    {
        _logins.Add(
            1,
            new KeyValuePair<string, object?>("provider", MetricTag.From(provider)),
            new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
    }

    /// <summary>
    /// Records one tenant SSO login failure, attributed to the tenant. Records the fleet-wide
    /// login series as well, so a caller never needs both calls.
    /// </summary>
    /// <param name="outcome">Rejected for a refused person, failed for a broken flow.</param>
    /// <param name="tenantId">The internal tenant id, or null where it was not resolved before the
    /// failure.</param>
    public void RecordSsoFailure(LoginOutcome outcome, int? tenantId)
    {
        RecordLogin(AuthProviderType.CustomOidc, outcome);

        string tenant = tenantId is null
            ? MetricTag.Unknown
            : tenantId.Value.ToString(CultureInfo.InvariantCulture);

        _ssoLoginFailures.Add(
            1,
            new KeyValuePair<string, object?>("tenant", tenant),
            new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (AuthProviderType provider in Enum.GetValues<AuthProviderType>())
        {
            foreach (LoginOutcome outcome in Enum.GetValues<LoginOutcome>())
            {
                _logins.Add(
                    0,
                    new KeyValuePair<string, object?>("provider", MetricTag.From(provider)),
                    new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
            }
        }
    }
}
