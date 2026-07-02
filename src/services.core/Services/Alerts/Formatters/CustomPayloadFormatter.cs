// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Services.Core.Alerts.Formatters;

/// <summary>
/// Formats alert payloads as raw JSON with HMAC-SHA256 signing for custom webhook endpoints.
/// The signature covers <c>"{unixSeconds}.{json}"</c> where <c>unixSeconds</c> is the value of the
/// <c>X-Vord-Timestamp</c> header, so receivers can bind a request to a point in time. Receivers
/// should recompute the HMAC over the same timestamped input and reject the delivery when
/// <c>|now − timestamp| > 300s</c> to defeat replay of a captured payload.
/// </summary>
public sealed class CustomPayloadFormatter : IIntegrationPayloadFormatter
{
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="CustomPayloadFormatter"/> class.
    /// </summary>
    /// <param name="provider">The data protection provider for decrypting webhook secrets.</param>
    /// <param name="timeProvider">The time source used to stamp each delivery for replay protection.</param>
    public CustomPayloadFormatter(IDataProtectionProvider provider, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _protector = provider.CreateProtector("IntegrationEndpointSecret");
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public IntegrationProvider Provider => IntegrationProvider.Custom;

    /// <inheritdoc/>
    public HttpRequestMessage FormatRequest(AlertEvent alertEvent, AlertRule rule, IntegrationEndpoint integration)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(integration);

        using JsonDocument config = JsonDocument.Parse(integration.Configuration);
        string url = config.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Custom configuration missing url");
        string encryptedSecret = config.RootElement.GetProperty("secret").GetString()
            ?? throw new InvalidOperationException("Custom configuration missing secret");

        string secret = _protector.Unprotect(encryptedSecret);

        object payload = new
        {
            eventId = alertEvent.Id,
            ruleName = rule.Name,
            severity = alertEvent.Severity.ToString(),
            message = alertEvent.Message,
            machineId = alertEvent.MachineId,
            triggeredAt = alertEvent.TriggeredAt,
            details = alertEvent.Details,
        };

        string json = JsonSerializer.Serialize(payload, JsonDefaults.CamelCase);

        long timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        string signingInput = $"{timestamp}.{json}";

        byte[] signatureBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(signingInput));
        string signature = $"sha256={Convert.ToHexStringLower(signatureBytes)}";

        HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Vord-Signature", signature);
        request.Headers.Add("X-Vord-Timestamp", timestamp.ToString());

        return request;
    }
}
