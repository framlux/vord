// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Interceptors;
using System.Data.Common;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Records the size of the widest statement a LinqToDB context sent, both the parameters it carried and
/// the length of its text, because an unbounded <c>IN</c> list shows up as one or the other: LinqToDB
/// writes a list of strings into the text as literals, and a list it parameterises would show as
/// parameters. A repository that chunks its lists never sends a statement much larger than one chunk.
/// Attach it with <c>AddInterceptor</c> before the code under test runs.
/// </summary>
public sealed class StatementSizeRecorder : CommandInterceptor
{
    private int _maxParameters;
    private int _maxTextLength;

    /// <summary>Gets the largest number of parameters any single statement carried.</summary>
    public int MaxParameters => _maxParameters;

    /// <summary>Gets the length of the longest statement text.</summary>
    public int MaxTextLength => _maxTextLength;

    /// <inheritdoc/>
    public override DbCommand CommandInitialized(CommandEventData eventData, DbCommand command)
    {
        _maxParameters = Math.Max(_maxParameters, command.Parameters.Count);
        _maxTextLength = Math.Max(_maxTextLength, command.CommandText.Length);

        return command;
    }
}
