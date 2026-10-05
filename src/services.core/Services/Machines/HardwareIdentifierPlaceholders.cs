// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Frozen;
using System.Text;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Decides whether a hardware identifier (a serial number or an asset tag) is firmware filler that
/// cannot tell one machine from another.
/// </summary>
/// <remarks>
/// <para>
/// Consumer and whitebox boards routinely ship with DMI fields nobody filled in, so many unrelated
/// machines report the same "System Serial Number" or "Default string". Treating such a value as an
/// identity makes the second machine of a tenant look like a duplicate of the first.
/// </para>
/// <para>
/// The list leans aggressive on purpose, and it should not be trimmed. A false positive only drops
/// the serial or asset-tag clause from the duplicate check: the system id (the machine-id of the
/// installed OS) is still compared, so a genuine re-registration of the same machine is still
/// caught. A false negative blocks every later machine that shares the filler value from
/// registering at all, which is the failure this class exists to prevent.
/// </para>
/// <para>
/// Matching is exact on a normalised form, never by substring, because a real serial may well
/// contain a word such as "serial". The Go agent carries the same rules in
/// <c>src/agent/internal/hwid</c>; the two lists must be kept in step.
/// </para>
/// </remarks>
public static class HardwareIdentifierPlaceholders
{
    // Stored in normalised form: lowercase, with spaces, '.', '-', '_' and '/' removed.
    private static readonly FrozenSet<string> KnownPlaceholders = new[]
    {
        "tobefilledbyoem",
        "notspecified",
        "notapplicable",
        "notavailable",
        "na",
        "none",
        "null",
        "unknown",
        "invalid",
        "undefined",
        "empty",
        "defaultstring",
        "default",
        "oem",
        "systemserialnumber",
        "chassisserialnumber",
        "baseboardserialnumber",
        "serialnumber",
        "noassettag",
        "noassetinformation",
        "assettag",
        "asset1234567890",
        "0123456789",
        "123456789",
        "1234567890",
        "12345678",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the value is firmware filler rather than something that identifies a machine.
    /// </summary>
    /// <param name="value">A serial number or asset tag as reported by the agent.</param>
    /// <returns>
    /// True when the value is null, blank, a known filler string, or a degenerate pattern such as
    /// all zeros; false when it could identify a machine.
    /// </returns>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string normalized = Normalize(value);

        if (normalized.Length == 0)
        {
            return true;
        }

        if (IsSingleRepeatedCharacter(normalized))
        {
            return true;
        }

        return KnownPlaceholders.Contains(normalized);
    }

    private static string Normalize(string value)
    {
        string lowered = value.ToLowerInvariant();
        StringBuilder builder = new(lowered.Length);

        foreach (char character in lowered)
        {
            if (IsSeparator(character) == false)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsSeparator(char character)
    {
        return (character == ' ') || (character == '.') || (character == '-') || (character == '_') || (character == '/');
    }

    private static bool IsSingleRepeatedCharacter(string value)
    {
        foreach (char character in value)
        {
            if (character != value[0])
            {
                return false;
            }
        }

        return true;
    }
}
