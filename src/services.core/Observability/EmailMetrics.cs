// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts outbound email by outcome and purpose, with failures additionally attributed to a tenant.
/// </summary>
/// <remarks>
/// This is the instrument the invitation-delivery incident would have caught: every send rejected
/// by the provider for as long as the configuration had been live, found by reading configuration
/// during unrelated work rather than by an alert.
///
/// Two counters rather than one conditional tag. A counter whose failure series acquires a tenant
/// dimension cannot have that series pre-created, so its first failure produces no observable
/// increase — the alert would miss exactly the event it exists for. The fleet-wide counter is dense
/// and pre-created and is what rules read; the attributed counter answers "which customer" and is
/// read by a query somebody runs deliberately.
/// </remarks>
public sealed class EmailMetrics : IInitialisableMetrics
{
    private const string UndeliverableOutcome = "undeliverable";

    private readonly Counter<long> _sends;
    private readonly Counter<long> _sendFailures;

    /// <summary>
    /// Creates the email instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public EmailMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _sends = meter.CreateCounter<long>(
            "vord.email.sends",
            description: "Outbound emails by delivery outcome and purpose.");

        _sendFailures = meter.CreateCounter<long>(
            "vord.email.send_failures",
            description: "Outbound email failures attributed to the tenant they were for.");
    }

    /// <summary>
    /// Records one attempted email delivery.
    /// </summary>
    /// <param name="outcome">The provider's verdict. Skipped means no provider is configured and is
    /// terminal success, never a failure.</param>
    /// <param name="purpose">What the email was for.</param>
    /// <param name="tenantId">The internal tenant id, or null where none is in scope. Used only
    /// when the outcome is a failure.</param>
    public void RecordSend(EmailDeliveryOutcome outcome, EmailPurpose purpose, int? tenantId)
    {
        KeyValuePair<string, object?> purposeTag = new("purpose", MetricTag.From(purpose));

        _sends.Add(1, new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)), purposeTag);

        if (outcome != EmailDeliveryOutcome.Failed)
        {
            return;
        }

        _sendFailures.Add(1, TenantTag(tenantId), purposeTag);
    }

    /// <summary>
    /// Records an email that was never attempted because there was nobody to send it to. No
    /// delivery outcome exists for this, but the customer receives nothing, which is the failure
    /// the instrument is for.
    /// </summary>
    /// <param name="purpose">What the email would have been.</param>
    /// <param name="tenantId">The internal tenant id, or null where none is in scope.</param>
    public void RecordUndeliverable(EmailPurpose purpose, int? tenantId)
    {
        KeyValuePair<string, object?> purposeTag = new("purpose", MetricTag.From(purpose));

        _sends.Add(1, new KeyValuePair<string, object?>("outcome", UndeliverableOutcome), purposeTag);
        _sendFailures.Add(1, TenantTag(tenantId), purposeTag);
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (EmailPurpose purpose in Enum.GetValues<EmailPurpose>())
        {
            KeyValuePair<string, object?> purposeTag = new("purpose", MetricTag.From(purpose));

            foreach (EmailDeliveryOutcome outcome in Enum.GetValues<EmailDeliveryOutcome>())
            {
                _sends.Add(0, new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)), purposeTag);
            }

            _sends.Add(0, new KeyValuePair<string, object?>("outcome", UndeliverableOutcome), purposeTag);

            // A real tenant's series cannot be pre-created, but the bucket used when no tenant is
            // in scope is known here, and it is the one an alert would otherwise miss the first
            // time it moved.
            _sendFailures.Add(0, TenantTag(null), purposeTag);
        }
    }

    private static KeyValuePair<string, object?> TenantTag(int? tenantId)
    {
        string value = tenantId is null
            ? MetricTag.Unknown
            : tenantId.Value.ToString(CultureInfo.InvariantCulture);

        return new KeyValuePair<string, object?>("tenant", value);
    }
}
