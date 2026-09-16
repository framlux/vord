// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.RegularExpressions;

namespace Framlux.FleetManagement.UnitTest.Architecture;

/// <summary>
/// Makes "every paginated endpoint reads its page size the same way" a constraint instead of a
/// convention.
/// </summary>
/// <remarks>
/// <para>
/// The page-size contract says an over-limit request is refused, never quietly reduced. It was
/// declared finished while three endpoints still carried their own <c>&gt; 100</c> literal and fell
/// back to the default instead — worse than the clamp it replaced, since a request for 200 rows got
/// 25. The contract test did not notice because it enumerated endpoints by hand, and the three were
/// not on the list.
/// </para>
/// <para>
/// This is a source check because nothing else can see the rule. Some endpoints read the page size
/// through a request DTO; others read the query string imperatively inside the handler and return a
/// plain list, so neither their request nor their response type says they paginate. The one thing
/// they all share is text: an endpoint that deals in a page size says so in its source.
/// </para>
/// </remarks>
public sealed class PageSizeReaderTests
{
    private const string SharedReader = "PageSizeQuery.TryResolve";

    private static readonly Regex MentionsPageSize = new(@"\bpageSize\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "machine-info.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate machine-info.slnx walking up from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> EndpointSources()
    {
        string endpoints = Path.Combine(FindRepoRoot(), "src", "server", "Endpoints");

        return Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) == false)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) == false)
            .Where(path => Path.GetFileName(path) != "PageSizeQuery.cs")
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    [Test]
    public async Task EveryEndpointThatHandlesAPageSize_ReadsItThroughTheSharedRule()
    {
        List<string> offenders = [];

        foreach (string path in EndpointSources())
        {
            string source = await File.ReadAllTextAsync(path);
            if (MentionsPageSize.IsMatch(source) && (source.Contains(SharedReader, StringComparison.Ordinal) == false))
            {
                offenders.Add(Path.GetFileName(path));
            }
        }

        await Assert.That(offenders)
            .IsEmpty()
            .Because("an endpoint that resolves its own page size can quietly serve a short page, which reads exactly like the end of the collection");
    }

    /// <summary>
    /// The scan above passes trivially if it reads nothing. It must at least see the endpoints known
    /// to paginate, including one whose types do not reveal it.
    /// </summary>
    [Test]
    public async Task TheScanSeesKnownPageSizeReaders()
    {
        HashSet<string> scanned = EndpointSources().Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);

        await Assert.That(scanned).Contains("MachineListEndpoint.cs");
        await Assert.That(scanned).Contains("CommandListEndpoint.cs");
        await Assert.That(scanned).Contains("AuditLogListEndpoint.cs");
    }
}
