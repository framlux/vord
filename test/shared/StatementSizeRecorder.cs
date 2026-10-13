// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Interceptors;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Records the size of the widest statement a LinqToDB context sent: the parameters it carried, the
/// length of its text and the most values any one <c>IN</c> list held, because an unbounded list shows
/// up as one or the other. LinqToDB writes a list of strings or ids into the text as literals, so
/// counting parameters alone cannot see it. It also counts the DELETE statements, which tells a delete
/// sent in chunks from one sent as a single statement. A repository that chunks its lists never sends a
/// statement much larger than one chunk. Attach it with <c>AddInterceptor</c> before the code under test
/// runs.
/// </summary>
public sealed partial class StatementSizeRecorder : CommandInterceptor
{
    private int _maxParameters;
    private int _maxTextLength;
    private int _maxInListItems;
    private int _deleteStatements;

    /// <summary>Gets the largest number of parameters any single statement carried.</summary>
    public int MaxParameters => _maxParameters;

    /// <summary>Gets the length of the longest statement text.</summary>
    public int MaxTextLength => _maxTextLength;

    /// <summary>Gets the most values any one <c>IN (...)</c> list of literals held.</summary>
    public int MaxInListItems => _maxInListItems;

    /// <summary>Gets how many DELETE statements were sent.</summary>
    public int DeleteStatements => _deleteStatements;

    /// <inheritdoc/>
    public override DbCommand CommandInitialized(CommandEventData eventData, DbCommand command)
    {
        string text = command.CommandText;
        _maxParameters = Math.Max(_maxParameters, command.Parameters.Count);
        _maxTextLength = Math.Max(_maxTextLength, text.Length);
        if (text.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            _deleteStatements++;
        }

        foreach (Match list in InList().Matches(text))
        {
            _maxInListItems = Math.Max(_maxInListItems, list.Groups[1].Value.Count(c => c == ',') + 1);
        }

        return command;
    }

    [GeneratedRegex(@"\bIN\s*\(([^()]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex InList();
}
