// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Frozen;
using System.Text;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Converts a closed enum member into the tag value written to a metric: lowercase, with words
/// separated by underscores.
/// </summary>
/// <remarks>
/// Every tag value in this system comes from an enum, never from caller-influenced text, because
/// an unbounded tag value is what turns a metric into a cardinality incident. The spelling matters
/// as much as the value: PromQL label matching is case-sensitive and silent on mismatch, so a rule
/// written against one spelling and a series emitted with another simply never fires. One helper,
/// tested once, is what stops two implementations disagreeing about it.
/// </remarks>
public static class MetricTag
{
    /// <summary>
    /// The defined bucket for a dimension whose value is genuinely not in scope at the recording
    /// site. An omitted tag would change the series shape; this keeps it stable.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Returns the snake_case tag value for an enum member.
    /// </summary>
    /// <typeparam name="TEnum">The closed enum the value comes from.</typeparam>
    /// <param name="value">The member to convert.</param>
    /// <returns>The lowercase, underscore-separated tag value.</returns>
    public static string From<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        if (TagValues<TEnum>.ByMember.TryGetValue(value, out string? tag) == true)
        {
            return tag;
        }

        return ToSnakeCase(value.ToString());
    }

    private static string ToSnakeCase(string name)
    {
        StringBuilder builder = new(name.Length + 4);

        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];

            if ((char.IsUpper(character) == true) && (index > 0))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Per-enum lookup built once on first use. Instruments are recorded on hot paths, and a
    /// dictionary keyed by the enum type itself avoids boxing the member on every measurement.
    /// </summary>
    private static class TagValues<TEnum>
        where TEnum : struct, Enum
    {
        internal static readonly FrozenDictionary<TEnum, string> ByMember =
            Enum.GetValues<TEnum>()
                .Distinct()
                .ToFrozenDictionary(member => member, member => ToSnakeCase(member.ToString()));
    }
}
