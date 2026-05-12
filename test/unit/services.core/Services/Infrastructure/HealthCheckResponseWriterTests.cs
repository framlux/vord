// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="HealthCheckResponseWriter"/>.
/// </summary>
public sealed class HealthCheckResponseWriterTests
{
    private static (DefaultHttpContext Context, MemoryStream Body) CreateTestContext()
    {
        MemoryStream body = new();
        DefaultHttpContext context = new();
        context.Response.Body = body;

        return (context, body);
    }

    private static HealthReport CreateReport(HealthStatus status, TimeSpan? totalDuration = null)
    {
        // HealthReport computes its Status from the worst entry, so we include an entry matching the desired status
        Dictionary<string, HealthReportEntry> entries = new()
        {
            ["probe"] = new HealthReportEntry(status, null, TimeSpan.Zero, null, null)
        };

        return new HealthReport(entries, totalDuration ?? TimeSpan.Zero);
    }

    private static string ReadResponseBody(MemoryStream body)
    {
        body.Seek(0, SeekOrigin.Begin);
        using StreamReader reader = new(body);

        return reader.ReadToEnd();
    }

    [Test]
    public async Task WriteMinimal_HealthyStatus_ReturnsCorrectJson()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = CreateReport(HealthStatus.Healthy);

        await HealthCheckResponseWriter.WriteMinimal(context, report);

        string json = ReadResponseBody(body);
        await Assert.That(json).IsEqualTo("{\"status\":\"Healthy\"}");
        await Assert.That(context.Response.ContentType).IsEqualTo("application/json");
    }

    [Test]
    public async Task WriteMinimal_UnhealthyStatus_ReturnsCorrectJson()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = CreateReport(HealthStatus.Unhealthy);

        await HealthCheckResponseWriter.WriteMinimal(context, report);

        string json = ReadResponseBody(body);
        await Assert.That(json).IsEqualTo("{\"status\":\"Unhealthy\"}");
    }

    [Test]
    public async Task WriteMinimal_DegradedStatus_ReturnsCorrectJson()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = CreateReport(HealthStatus.Degraded);

        await HealthCheckResponseWriter.WriteMinimal(context, report);

        string json = ReadResponseBody(body);
        await Assert.That(json).IsEqualTo("{\"status\":\"Degraded\"}");
    }

    [Test]
    public async Task WriteDetailed_IncludesAllEntriesWithDurations()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();

        Dictionary<string, HealthReportEntry> entries = new()
        {
            ["db"] = new HealthReportEntry(HealthStatus.Healthy, "database is ok", TimeSpan.FromMilliseconds(50), null, null),
            ["redis"] = new HealthReportEntry(HealthStatus.Degraded, "redis is slow", TimeSpan.FromMilliseconds(200), null, null)
        };
        HealthReport report = new(entries, TimeSpan.FromMilliseconds(250));

        await HealthCheckResponseWriter.WriteDetailed(context, report);

        string json = ReadResponseBody(body);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("entries").GetProperty("db").GetProperty("description").GetString()).IsEqualTo("database is ok");
        await Assert.That(root.GetProperty("entries").GetProperty("redis").GetProperty("description").GetString()).IsEqualTo("redis is slow");
        await Assert.That(root.GetProperty("entries").GetProperty("db").GetProperty("duration").GetDouble()).IsEqualTo(50.0);
        await Assert.That(root.GetProperty("entries").GetProperty("redis").GetProperty("duration").GetDouble()).IsEqualTo(200.0);
    }

    [Test]
    public async Task WriteDetailed_UsesCamelCasePropertyNames()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.FromSeconds(1));

        await HealthCheckResponseWriter.WriteDetailed(context, report);

        string json = ReadResponseBody(body);
        await Assert.That(json).Contains("totalDuration");
        await Assert.That(json).DoesNotContain("TotalDuration");
    }

    [Test]
    public async Task WriteDetailed_ZeroEntries_ReturnsEmptyEntriesObject()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

        await HealthCheckResponseWriter.WriteDetailed(context, report);

        string json = ReadResponseBody(body);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement entries = doc.RootElement.GetProperty("entries");

        await Assert.That(entries.EnumerateObject().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task WriteDetailed_NullDescription_SerializesWithoutError()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();

        Dictionary<string, HealthReportEntry> entries = new()
        {
            ["check"] = new HealthReportEntry(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(10), null, null)
        };
        HealthReport report = new(entries, TimeSpan.FromMilliseconds(10));

        await HealthCheckResponseWriter.WriteDetailed(context, report);

        string json = ReadResponseBody(body);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement checkEntry = doc.RootElement.GetProperty("entries").GetProperty("check");

        await Assert.That(checkEntry.TryGetProperty("description", out _)).IsFalse();
    }

    [Test]
    public async Task WriteDetailed_TotalDuration_IsInMilliseconds()
    {
        (DefaultHttpContext context, MemoryStream body) = CreateTestContext();
        HealthReport report = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.FromSeconds(1.5));

        await HealthCheckResponseWriter.WriteDetailed(context, report);

        string json = ReadResponseBody(body);
        using JsonDocument doc = JsonDocument.Parse(json);
        double totalDuration = doc.RootElement.GetProperty("totalDuration").GetDouble();

        await Assert.That(totalDuration).IsEqualTo(1500.0);
    }
}
