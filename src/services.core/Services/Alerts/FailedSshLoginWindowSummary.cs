// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// The aggregates that describe one failed-SSH-login window, serialized into an alert event's
/// details.
/// </summary>
/// <remarks>
/// A window is not an attempt. It gathers many attempts from many addresses against many user names,
/// so carrying one attempt's fields would describe an arbitrary member of the set and read as though
/// it described the whole incident. Every singular field here is therefore defined explicitly:
/// <see cref="TopSourceIp"/> is the address with the most attempts in the sample, ties broken toward
/// the most recent attempt.
/// </remarks>
public sealed record FailedSshLoginWindowSummary
{
    /// <summary>Number of failed attempts in the window, counted by the database.</summary>
    public required int FailureCount { get; init; }

    /// <summary>Length of the evaluated window in minutes — the rule's own DurationMinutes.</summary>
    public required int WindowMinutes { get; init; }

    /// <summary>Inclusive start of the window, in server receipt time.</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>Inclusive end of the window, in server receipt time.</summary>
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>
    /// How many attempts the aggregates below were computed from. Equal to
    /// <see cref="FailureCount"/> unless the window held more attempts than the sample cap.
    /// </summary>
    public required int SampledAttempts { get; init; }

    /// <summary>Number of distinct source addresses in the sample.</summary>
    public required int DistinctSourceIpCount { get; init; }

    /// <summary>The source address with the most attempts in the sample; ties go to the most recent.</summary>
    public required string TopSourceIp { get; init; }

    /// <summary>Attempts made from <see cref="TopSourceIp"/> within the sample.</summary>
    public required int TopSourceIpAttempts { get; init; }

    /// <summary>Number of distinct user names targeted in the sample.</summary>
    public required int DistinctUserCount { get; init; }

    /// <summary>The targeted user names, most recently seen first, capped for readability.</summary>
    public required IReadOnlyList<string> Users { get; init; }

    /// <summary>
    /// Builds the summary from a window's sampled attempts, newest first.
    /// </summary>
    /// <param name="failureCount">The database's count for the whole window.</param>
    /// <param name="windowMinutes">The rule's window length in minutes.</param>
    /// <param name="windowStart">Inclusive start of the window.</param>
    /// <param name="windowEnd">Inclusive end of the window.</param>
    /// <param name="attempts">The sampled attempts, ordered most recent first.</param>
    /// <returns>The window's aggregates.</returns>
    public static FailedSshLoginWindowSummary Create(
        int failureCount,
        int windowMinutes,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<SshSessionPayload> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        Dictionary<string, int> attemptsPerIp = new(StringComparer.Ordinal);
        List<string> sourceIpsMostRecentFirst = [];
        List<string> users = [];
        HashSet<string> seenUsers = new(StringComparer.Ordinal);

        foreach (SshSessionPayload attempt in attempts)
        {
            string sourceIp = attempt.SourceIp;

            if (attemptsPerIp.TryGetValue(sourceIp, out int seen))
            {
                attemptsPerIp[sourceIp] = seen + 1;
            }
            else
            {
                attemptsPerIp[sourceIp] = 1;
                sourceIpsMostRecentFirst.Add(sourceIp);
            }

            if (seenUsers.Add(attempt.User))
            {
                users.Add(attempt.User);
            }
        }

        string topSourceIp = "";
        int topSourceIpAttempts = 0;

        // Candidates are walked in most-recently-seen order and the comparison is strictly greater,
        // so an address that ties on attempt count loses to the more recent attacker.
        foreach (string candidate in sourceIpsMostRecentFirst)
        {
            int candidateAttempts = attemptsPerIp[candidate];

            if (candidateAttempts > topSourceIpAttempts)
            {
                topSourceIp = candidate;
                topSourceIpAttempts = candidateAttempts;
            }
        }

        return new FailedSshLoginWindowSummary
        {
            FailureCount = failureCount,
            WindowMinutes = windowMinutes,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            SampledAttempts = attempts.Count,
            DistinctSourceIpCount = attemptsPerIp.Count,
            TopSourceIp = topSourceIp,
            TopSourceIpAttempts = topSourceIpAttempts,
            DistinctUserCount = users.Count,
            Users = users.Count > AlertConstants.FailedSshLoginDetailUserLimit
                ? users.GetRange(0, AlertConstants.FailedSshLoginDetailUserLimit)
                : users,
        };
    }
}
