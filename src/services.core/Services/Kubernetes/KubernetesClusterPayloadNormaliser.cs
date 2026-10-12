// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Validates and normalises a stored cluster snapshot (telemetry type 15) so that no value can exceed the
/// column it will be written to or the view it will be shown in, and turns it into the snapshot the
/// inventory applies. The snapshot is sent by an agent the server does not control, so everything that
/// identifies or orders a cluster is checked here and nothing is trusted to be well formed.
/// </summary>
internal static class KubernetesClusterPayloadNormaliser
{
    private const int KubeSystemUidLength = 36;
    private const int MaxClusterVersionLength = 64;
    private const int MaxLeaseUidLength = 64;
    private const int MaxMachineIdLength = 64;
    private const int MaxRuntimeVersionLength = 64;
    private const int MaxReadyStatusLength = 16;
    private const int MaxRoles = 16;
    private const int MaxRoleLength = 63;
    private const int MaxConditions = 16;
    private const int MaxConditionTypeLength = 63;
    private const int MaxConditionStatusLength = 16;
    private const string ApiUnreachable = "api_unreachable";

    /// <summary>
    /// How far after the server received a snapshot its Lease may claim to have been created. The agent's
    /// clock is not the server's, but a Lease cannot postdate the snapshot that describes it by more than
    /// the clocks can differ.
    /// </summary>
    private static readonly TimeSpan MaxLeaseCreatedAfterReceipt = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> KnownReporterErrors = new(StringComparer.Ordinal)
    {
        "credentials_rejected",
        "tls_ca_changed",
        ApiUnreachable,
        "namespace_terminating",
    };

    /// <summary>
    /// Normalises a deserialized snapshot.
    /// <list type="bullet">
    /// <item>
    /// A snapshot is unusable, and null is returned, when the Lease it was taken under has no readable
    /// creation time, a creation time more than ten minutes after the server received the snapshot, or a
    /// negative transition count: they order the snapshot against the cluster's others, and a snapshot
    /// that cannot be ordered cannot be fenced. A creation time in the far future would outrank every
    /// later snapshot of the cluster.
    /// </item>
    /// <item>A UID that is not a UUID and a CA fingerprint that is not 64 lowercase hex characters become null.</item>
    /// <item>A reporter error that is none of the four codes the agent sends becomes <c>api_unreachable</c>.</item>
    /// <item>An error snapshot lists no nodes.</item>
    /// <item>Strings longer than their column are cut, and nodes whose name is not a Kubernetes node name are dropped.</item>
    /// </list>
    /// </summary>
    /// <param name="payload">The deserialized payload.</param>
    /// <param name="row">The telemetry row the payload came from.</param>
    /// <returns>The snapshot to apply, or null when it cannot be ordered against the cluster's others.</returns>
    public static ClusterSnapshot? Normalise(KubernetesClusterPayload payload, MachineTelemetry row)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(row);

        DateTimeOffset? leaseCreatedAt = ParseTimestamp(payload.LeaseCreatedAt);
        if ((leaseCreatedAt == null) || (payload.LeaseTransitions < 0) || (leaseCreatedAt.Value > (row.ServerReceivedAt + MaxLeaseCreatedAfterReceipt)))
        {
            return null;
        }

        string? reporterError = NormaliseReporterError(payload.ReporterError);
        List<ClusterSnapshotNode> nodes = [];
        if (reporterError == null)
        {
            foreach (KubernetesClusterNodeDto node in payload.Nodes ?? [])
            {
                ClusterSnapshotNode? normalised = NormaliseNode(node);
                if (normalised != null)
                {
                    nodes.Add(normalised);
                }
            }
        }

        return new ClusterSnapshot(
            row.Id,
            row.TenantId,
            row.MachineId,
            row.ServerReceivedAt,
            NormaliseKubeSystemUid(payload.KubeSystemUid),
            KubernetesNodePayloadNormaliser.NormaliseCaFingerprint(payload.CaFingerprint),
            Truncate(payload.ClusterVersion, MaxClusterVersionLength),
            payload.RoleOutdated,
            payload.LeaseTransitions,
            Truncate(payload.LeaseUid, MaxLeaseUidLength),
            leaseCreatedAt.Value,
            reporterError,
            payload.Truncated,
            nodes);
    }

    private static ClusterSnapshotNode? NormaliseNode(KubernetesClusterNodeDto? node)
    {
        if ((node is null) || (node.Name is null) || (KubernetesNodePayloadNormaliser.IsValidNodeName(node.Name) == false))
        {
            return null;
        }

        List<string> roles = [];
        foreach (string? role in node.Roles ?? [])
        {
            string? text = Truncate(role, MaxRoleLength);
            if ((text != null) && (roles.Contains(text, StringComparer.Ordinal) == false) && (roles.Count < MaxRoles))
            {
                roles.Add(text);
            }
        }

        List<ClusterSnapshotCondition> conditions = [];
        foreach (KubernetesNodeConditionDto? condition in node.Conditions ?? [])
        {
            string? type = Truncate(condition?.Type, MaxConditionTypeLength);
            if ((type == null) || (conditions.Count >= MaxConditions))
            {
                continue;
            }

            conditions.Add(new ClusterSnapshotCondition(
                type,
                Truncate(condition!.Status, MaxConditionStatusLength) ?? string.Empty,
                ParseTimestamp(condition.LastTransitionTime)));
        }

        return new ClusterSnapshotNode(
            node.Name,
            Truncate(node.MachineId, MaxMachineIdLength),
            roles,
            Truncate(node.ReadyStatus, MaxReadyStatusLength),
            ParseTimestamp(node.ReadySince),
            conditions,
            node.Unschedulable,
            KubernetesNodePayloadNormaliser.NormaliseKubeletVersion(node.KubeletVersion),
            Truncate(node.ContainerRuntimeVersion, MaxRuntimeVersionLength),
            Math.Max(0, node.PodCount),
            (node.PodCapacity > 0) ? node.PodCapacity : null);
    }

    /// <summary>
    /// Accepts a UID in the canonical form of a UUID (8-4-4-4-12 hexadecimal digits), which is what
    /// Kubernetes assigns, and returns it in lowercase. Anything else is not a cluster identity.
    /// </summary>
    private static string? NormaliseKubeSystemUid(string? uid)
    {
        if ((uid is null) || (uid.Length != KubeSystemUidLength))
        {
            return null;
        }

        for (int index = 0; index < uid.Length; index++)
        {
            bool dashPosition = (index == 8) || (index == 13) || (index == 18) || (index == 23);
            char character = uid[index];
            bool valid = dashPosition ? (character == '-') : char.IsAsciiHexDigit(character);
            if (valid == false)
            {
                return null;
            }
        }

        return uid.ToLowerInvariant();
    }

    private static string? NormaliseReporterError(string? reporterError)
    {
        if (string.IsNullOrWhiteSpace(reporterError))
        {
            return null;
        }

        return KnownReporterErrors.Contains(reporterError) ? reporterError : ApiUnreachable;
    }

    /// <summary>
    /// Reads an ISO 8601 timestamp as the instant it names. A value that is empty or is not a timestamp is
    /// null, and a timestamp with no offset is read as UTC.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        bool parsed = DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp);

        return parsed ? timestamp : null;
    }

    /// <summary>
    /// Cuts a string to a column's length, or returns null for an empty one. The cut never lands
    /// between the halves of a surrogate pair, which no database encodes.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        int length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
