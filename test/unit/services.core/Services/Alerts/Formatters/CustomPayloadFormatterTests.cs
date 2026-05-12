// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Alerts.Formatters;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Test.Services.Alerts.Formatters;

/// <summary>
/// Tests for <see cref="CustomPayloadFormatter"/>.
/// </summary>
public sealed class CustomPayloadFormatterTests
{
    private static readonly IDataProtectionProvider TestProtectionProvider = new EphemeralDataProtectionProvider();

    private readonly CustomPayloadFormatter _formatter = new(TestProtectionProvider);

    private static string EncryptSecret(string plaintext)
    {
        IDataProtector protector = TestProtectionProvider.CreateProtector("IntegrationEndpointSecret");

        return protector.Protect(plaintext);
    }

    private static AlertEvent CreateEvent()
    {
        return new AlertEvent
        {
            Id = 100,
            AlertRuleId = 1,
            TenantId = 1,
            MachineId = 42,
            Severity = AlertSeverity.Critical,
            Message = "CPU at 95%",
            Details = """{"metric":"CpuUsage","currentValue":95}""",
            Status = AlertEventStatus.Triggered,
            TriggeredAt = DateTimeOffset.Parse("2026-05-10T12:30:00+00:00"),
        };
    }

    private static AlertRule CreateRule()
    {
        return new AlertRule
        {
            Id = 1,
            TenantId = 1,
            Name = "High CPU Alert",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 80,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            NotifyWebhook = true,
            IsCustom = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static IntegrationEndpoint CreateIntegration(string secret = "my-test-secret")
    {
        string encryptedSecret = EncryptSecret(secret);

        return new IntegrationEndpoint
        {
            Id = 1,
            TenantId = 1,
            Provider = IntegrationProvider.Custom,
            Name = "Custom Integration",
            Configuration = JsonSerializer.Serialize(new { url = "https://hooks.example.com/custom", secret = encryptedSecret }),
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task Provider_ReturnsCustom()
    {
        await Assert.That(_formatter.Provider).IsEqualTo(IntegrationProvider.Custom);
    }

    [Test]
    public async Task FormatRequest_SetsCorrectUrlFromConfiguration()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://hooks.example.com/custom");
    }

    [Test]
    public async Task FormatRequest_PayloadContainsExpectedFields()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());
        string body = await request.Content!.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        await Assert.That(root.TryGetProperty("eventId", out JsonElement _)).IsTrue();
        await Assert.That(root.TryGetProperty("ruleName", out JsonElement _)).IsTrue();
        await Assert.That(root.TryGetProperty("severity", out JsonElement _)).IsTrue();
        await Assert.That(root.TryGetProperty("message", out JsonElement _)).IsTrue();
        await Assert.That(root.TryGetProperty("machineId", out JsonElement _)).IsTrue();
        await Assert.That(root.TryGetProperty("triggeredAt", out JsonElement _)).IsTrue();
    }

    [Test]
    public async Task FormatRequest_SignatureHeaderIsPresent()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        bool hasHeader = request.Headers.Contains("X-Vord-Signature");

        await Assert.That(hasHeader).IsTrue();
    }

    [Test]
    public async Task FormatRequest_SignatureStartsWithSha256Prefix()
    {
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration());

        string signature = request.Headers.GetValues("X-Vord-Signature").First();

        await Assert.That(signature.StartsWith("sha256=", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task FormatRequest_SignatureIsValidHmacSha256()
    {
        string secret = "my-test-secret";
        HttpRequestMessage request = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration(secret));
        string body = await request.Content!.ReadAsStringAsync();
        string signature = request.Headers.GetValues("X-Vord-Signature").First();

        // Strip the sha256= prefix
        string hexPart = signature.Substring("sha256=".Length);

        // Compute the expected HMAC-SHA256
        byte[] expectedSig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(body));
        string expectedHex = Convert.ToHexStringLower(expectedSig);

        await Assert.That(hexPart).IsEqualTo(expectedHex);
    }

    [Test]
    public async Task FormatRequest_DifferentSecrets_ProduceDifferentSignatures()
    {
        HttpRequestMessage request1 = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration("secret-one"));
        HttpRequestMessage request2 = _formatter.FormatRequest(CreateEvent(), CreateRule(), CreateIntegration("secret-two"));

        string sig1 = request1.Headers.GetValues("X-Vord-Signature").First();
        string sig2 = request2.Headers.GetValues("X-Vord-Signature").First();

        await Assert.That(sig1).IsNotEqualTo(sig2);
    }

    [Test]
    public async Task FormatRequest_NullAlertEvent_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(null!, CreateRule(), CreateIntegration())).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_NullRule_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), null!, CreateIntegration())).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_NullIntegration_ThrowsArgumentNullException()
    {
        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), CreateRule(), null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    public async Task FormatRequest_MissingUrlKey_ThrowsKeyNotFoundException()
    {
        IntegrationEndpoint integration = new()
        {
            Id = 1,
            TenantId = 1,
            Provider = IntegrationProvider.Custom,
            Name = "Custom Integration",
            Configuration = "{}",
            IsEnabled = true,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await Assert.That(() => _formatter.FormatRequest(CreateEvent(), CreateRule(), integration)).ThrowsExactly<KeyNotFoundException>();
    }
}
