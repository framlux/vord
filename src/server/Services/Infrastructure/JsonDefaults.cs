// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Encodings.Web;
using System.Text.Json;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Centralized JSON serializer options shared across the fleet server.
/// </summary>
public static class JsonDefaults
{
    /// <summary>
    /// Shared camelCase JSON options with relaxed Unicode escaping.
    /// UnsafeRelaxedJsonEscaping is safe here because all API responses are served as
    /// application/json and consumed by SvelteKit, which auto-escapes HTML in templates.
    /// JsonSerializerOptions is thread-safe for reads after first use by the serializer.
    /// </summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Shared snake_case JSON options with relaxed Unicode escaping.
    /// Used for the telemetry pipeline where data originates from the Go fleet agent,
    /// which follows Go conventions for JSON serialization (snake_case struct tags).
    /// </summary>
    public static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
