// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Builds the canonical payload a signed remote command carries: the JSON object with the nine keys the
/// agent reads, in the order the web client writes them. The server only compares <c>command_type</c> and
/// <c>params</c> with the request, so the other fields default to placeholders.
/// </summary>
public static class TestCanonicalPayload
{
    /// <summary>
    /// Builds a canonical payload.
    /// </summary>
    /// <param name="commandType">The signed command type, written exactly as given.</param>
    /// <param name="paramsJson">The raw JSON written for <c>params</c>; an empty object by default, and passed through
    /// untouched so a test can sign a shape the server must refuse.</param>
    /// <param name="commandId">The signed command id; a new UUID when omitted.</param>
    /// <param name="nonce">The signed nonce; 32 hex characters when omitted.</param>
    /// <param name="machineId">The signed machine id.</param>
    /// <param name="tenantId">The signed tenant id.</param>
    /// <param name="userId">The signed user id.</param>
    public static string Build(
        string commandType,
        string paramsJson = "{}",
        string? commandId = null,
        string? nonce = null,
        long machineId = 1,
        int tenantId = 1,
        int userId = 1)
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = timestamp.AddMinutes(10);

        return "{"
            + $"\"command_id\":{Quote(commandId ?? Guid.NewGuid().ToString("D"))},"
            + $"\"command_type\":{Quote(commandType)},"
            + $"\"expires_at\":{Quote(expiresAt.ToString("o", CultureInfo.InvariantCulture))},"
            + $"\"machine_id\":{machineId.ToString(CultureInfo.InvariantCulture)},"
            + $"\"nonce\":{Quote(nonce ?? Guid.NewGuid().ToString("N"))},"
            + $"\"params\":{paramsJson},"
            + $"\"tenant_id\":{tenantId.ToString(CultureInfo.InvariantCulture)},"
            + $"\"timestamp\":{Quote(timestamp.ToString("o", CultureInfo.InvariantCulture))},"
            + $"\"user_id\":{userId.ToString(CultureInfo.InvariantCulture)}"
            + "}";
    }

    private static string Quote(string value)
    {
        return JsonSerializer.Serialize(value);
    }
}
