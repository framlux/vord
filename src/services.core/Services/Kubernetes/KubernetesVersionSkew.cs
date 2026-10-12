// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Decides whether a node's kubelet is far enough from its cluster's version to be flagged.
/// </summary>
public static class KubernetesVersionSkew
{
    /// <summary>
    /// How many minor versions a kubelet may trail the cluster before it is flagged. Kubernetes supports a
    /// kubelet up to three minor versions older than the API server.
    /// </summary>
    private const int MaxMinorVersionsBehind = 3;

    /// <summary>
    /// Whether the kubelet is more than three minor versions behind the cluster, or newer than it. Only the
    /// minor version counts, because Kubernetes has not changed its major version, and nothing is flagged
    /// unless both versions can be read.
    /// </summary>
    /// <param name="kubeletVersion">The node's kubelet version, such as <c>v1.30.4+k3s1</c>.</param>
    /// <param name="clusterVersion">The cluster's server version.</param>
    /// <returns>True when the kubelet's version is outside what the cluster supports.</returns>
    public static bool IsKubeletSkewed(string? kubeletVersion, string? clusterVersion)
    {
        if ((TryParseMinor(kubeletVersion, out int kubeletMinor) == false) || (TryParseMinor(clusterVersion, out int clusterMinor) == false))
        {
            return false;
        }

        return (kubeletMinor > clusterMinor) || ((clusterMinor - kubeletMinor) > MaxMinorVersionsBehind);
    }

    /// <summary>
    /// Reads the minor version from text shaped like <c>v1.30.4+k3s1</c>: an optional <c>v</c>, a major and a
    /// minor number, then nothing or a patch number and any suffix. Done by hand so that hostile text can
    /// never make the parse expensive.
    /// </summary>
    private static bool TryParseMinor(string? version, out int minor)
    {
        minor = 0;
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        int position = (version[0] == 'v') ? 1 : 0;
        if (ReadNumber(version, ref position, out _) == false)
        {
            return false;
        }

        if ((position >= version.Length) || (version[position] != '.'))
        {
            return false;
        }

        position++;
        if (ReadNumber(version, ref position, out minor) == false)
        {
            return false;
        }

        // What follows the minor number must start a patch or a suffix, so "1.30x" is not a version.
        return (position >= version.Length) || (version[position] == '.') || (version[position] == '+') || (version[position] == '-');
    }

    private static bool ReadNumber(string text, ref int position, out int value)
    {
        value = 0;
        int start = position;
        while ((position < text.Length) && (text[position] >= '0') && (text[position] <= '9'))
        {
            // Nine digits is far beyond any real version and keeps the value inside an int.
            if ((position - start) >= 9)
            {
                return false;
            }

            value = (value * 10) + (text[position] - '0');
            position++;
        }

        return position > start;
    }
}
