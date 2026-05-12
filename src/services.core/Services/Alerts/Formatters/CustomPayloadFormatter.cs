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
/// </summary>
public sealed class CustomPayloadFormatter : IIntegrationPayloadFormatter
{
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates a new instance of the <see cref="CustomPayloadFormatter"/> class.
    /// </summary>
    /// <param name="provider">The data protection provider for decrypting webhook secrets.</param>
    public CustomPayloadFormatter(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("IntegrationEndpointSecret");
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

        byte[] signatureBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(json));
        string signature = $"sha256={Convert.ToHexStringLower(signatureBytes)}";

        HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Vord-Signature", signature);

        return request;
    }
}
