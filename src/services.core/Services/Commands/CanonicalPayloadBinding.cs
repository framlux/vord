// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Commands;

/// <summary>
/// Compares what a signed canonical payload says with what the request that carries it says. The agent
/// runs the command type and parameters it reads from the signed bytes, so a request that names something
/// else would be judged by the server (the Team check, the eligibility check) as a command that never runs.
/// </summary>
/// <remarks>
/// The reading follows the agent's (<c>ParseCanonicalPayload</c> and <c>BindsTo</c> in the agent's crypto
/// package) for the two fields compared: the payload is one JSON object, no key repeats, a key that differs
/// from <c>command_type</c> or <c>params</c> only in case is an error (a case-insensitive reader would take
/// it for the real one), <c>command_type</c> is a string, and <c>params</c> is an object of unique string
/// values with an absent <c>params</c> meaning none. The id, tenant, machine, user, nonce and timestamps are
/// bound by the agent against the command it is delivered, so the server leaves them alone.
/// </remarks>
internal static class CanonicalPayloadBinding
{
    private const string CommandTypeKey = "command_type";
    private const string ParamsKey = "params";

    /// <summary>
    /// Reports whether the payload's <c>command_type</c> equals the request's ordinally and its <c>params</c>
    /// equal the request's parameters as maps, where null or blank request parameters equal an empty map.
    /// </summary>
    /// <param name="canonicalPayload">The exact canonical JSON that was signed.</param>
    /// <param name="commandType">The command type the request names.</param>
    /// <param name="commandParams">The JSON-encoded parameters the request carries, or null.</param>
    /// <returns>True only when the payload is readable and agrees with the request on both.</returns>
    internal static bool Matches(string canonicalPayload, string commandType, string? commandParams)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);
        ArgumentNullException.ThrowIfNull(commandType);

        if (CommandParameters.TryParse(commandParams, out Dictionary<string, string> requested) == false)
        {
            return false;
        }

        if (TryReadSigned(canonicalPayload, out string signedType, out Dictionary<string, string> signed) == false)
        {
            return false;
        }

        if (string.Equals(signedType, commandType, StringComparison.Ordinal) == false)
        {
            return false;
        }

        return AreEqual(signed, requested);
    }

    private static bool TryReadSigned(string canonicalPayload, out string signedType, out Dictionary<string, string> signedParams)
    {
        signedType = string.Empty;
        signedParams = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(canonicalPayload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            string? type = null;
            Dictionary<string, string> readParams = [];

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (seen.Add(property.Name) == false)
                {
                    return false;
                }

                if (IsCaseVariant(property.Name, CommandTypeKey) || IsCaseVariant(property.Name, ParamsKey))
                {
                    return false;
                }

                if (string.Equals(property.Name, CommandTypeKey, StringComparison.Ordinal))
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    type = property.Value.GetString();
                }
                else if (string.Equals(property.Name, ParamsKey, StringComparison.Ordinal))
                {
                    if (CommandParameters.TryRead(property.Value, out readParams) == false)
                    {
                        return false;
                    }
                }
            }

            if (type is null)
            {
                return false;
            }

            signedType = type;
            signedParams = readParams;

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsCaseVariant(string name, string knownKey)
    {
        bool sameIgnoringCase = string.Equals(name, knownKey, StringComparison.OrdinalIgnoreCase);

        return sameIgnoringCase && (string.Equals(name, knownKey, StringComparison.Ordinal) == false);
    }

    private static bool AreEqual(Dictionary<string, string> first, Dictionary<string, string> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in first)
        {
            if ((second.TryGetValue(pair.Key, out string? other) == false) || (string.Equals(pair.Value, other, StringComparison.Ordinal) == false))
            {
                return false;
            }
        }

        return true;
    }
}
