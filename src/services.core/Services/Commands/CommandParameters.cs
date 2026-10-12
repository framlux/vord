// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Commands;

/// <summary>
/// The one definition of a remote command's parameters: a JSON object whose every value is a string and
/// whose keys are unique. It is the shape the agent reads from a signed payload, which refuses any other,
/// so the request validator, the payload comparison and the delivery path all judge it the same way.
/// </summary>
public static class CommandParameters
{
    /// <summary>
    /// Reads command parameters from JSON text. Null and blank text mean no parameters and read as an empty
    /// map. Anything that is not an object of string values, including a duplicate key (which a lenient
    /// reader would resolve by silently keeping one of the two), does not parse.
    /// </summary>
    /// <param name="json">The JSON-encoded parameters as submitted, or null.</param>
    /// <param name="parameters">The parameters keyed ordinally, or an empty map when the text does not parse.</param>
    /// <returns>True when the text is absent, blank, or an object of unique string values.</returns>
    public static bool TryParse(string? json, out Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            parameters = [];

            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            return TryRead(document.RootElement, out parameters);
        }
        catch (JsonException)
        {
            parameters = [];

            return false;
        }
    }

    /// <summary>
    /// Reads command parameters from a parsed JSON value, with the same rules as <see cref="TryParse"/>
    /// except that the value must be present: only an object of unique string values reads.
    /// </summary>
    /// <param name="element">The JSON value to read.</param>
    /// <param name="parameters">The parameters keyed ordinally, or an empty map when the value does not read.</param>
    internal static bool TryRead(JsonElement element, out Dictionary<string, string> parameters)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string? value = ReadString(property.Value);
                if ((value is null) || (result.TryAdd(property.Name, value) == false))
                {
                    parameters = [];

                    return false;
                }
            }

            parameters = result;

            return true;
        }

        parameters = [];

        return false;
    }

    /// <summary>
    /// Reads a JSON string value, or null for any other kind and for text that cannot be decoded, such as
    /// a lone surrogate escape.
    /// </summary>
    private static string? ReadString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
