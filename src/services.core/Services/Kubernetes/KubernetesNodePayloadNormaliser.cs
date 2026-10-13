// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Validates and normalises a stored Kubernetes node report so that no value can exceed the column it
/// will be written to or the view it will be shown in. The inventory projection and the machine page
/// both read the same stored payload, so they share this one definition of what a usable report is. The
/// cluster snapshot normaliser reuses the node name, fingerprint and kubelet version rules.
/// </summary>
internal static class KubernetesNodePayloadNormaliser
{
    private const int MaxNodeNameLength = 253;
    private const int MaxKubeletVersionLength = 64;
    private const int CaFingerprintLength = 64;
    private const string UnknownDistro = "unknown";

    private static readonly HashSet<string> KnownDistros = new(StringComparer.Ordinal)
    {
        "k3s",
        "rke2",
        "kubeadm",
        "k0s",
        "microk8s",
        UnknownDistro,
    };

    private static readonly HashSet<string> KnownReporterErrors = new(StringComparer.Ordinal)
    {
        "credentials_rejected",
        "tls_ca_changed",
        "state_copied",
    };

    /// <summary>
    /// Normalises a deserialized node report. A report must name a distro: an empty one is what a
    /// payload the server failed to serialise looks like, not something an agent sends. The node name
    /// must be a valid Kubernetes node name, except that a host without a kubelet may report an empty
    /// one, because a control-plane host with no kubelet and an unusable hostname has no name to give.
    /// An unrecognised distro becomes <c>unknown</c>, a CA fingerprint that is not 64 lowercase hex
    /// characters and an empty kubelet version become empty, and a longer version is cut to fit. A
    /// reporter error is kept only when it is one of the three codes the agent sends in a local report
    /// (<c>credentials_rejected</c>, <c>tls_ca_changed</c> and <c>state_copied</c>), and is null otherwise.
    /// </summary>
    /// <param name="payload">The deserialized payload.</param>
    /// <returns>The normalised report, or null when it names no distro or has an unusable node name.</returns>
    public static KubernetesNodePayload? Normalise(KubernetesNodePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrEmpty(payload.Distro))
        {
            return null;
        }

        string nodeName = payload.NodeName ?? string.Empty;
        bool nameIsUsable;
        if (nodeName.Length == 0)
        {
            nameIsUsable = payload.HasKubelet == false;
        }
        else
        {
            nameIsUsable = IsValidNodeName(nodeName);
        }

        if (nameIsUsable == false)
        {
            return null;
        }

        return new KubernetesNodePayload
        {
            Distro = NormaliseDistro(payload.Distro),
            KubeletVersion = NormaliseKubeletVersion(payload.KubeletVersion) ?? string.Empty,
            NodeName = nodeName,
            NodeNameSource = payload.NodeNameSource ?? string.Empty,
            HasKubelet = payload.HasKubelet,
            ControlPlane = payload.ControlPlane,
            CaFingerprint = NormaliseCaFingerprint(payload.CaFingerprint) ?? string.Empty,
            AdminCredentialPresent = payload.AdminCredentialPresent,
            ReporterEnrolled = payload.ReporterEnrolled,
            ReporterError = NormaliseReporterError(payload.ReporterError),
        };
    }

    /// <summary>
    /// Reads a stored node report and normalises it, so that every reader of the stored text agrees on what
    /// counts as a report. Text that is not a report, a JSON <c>null</c>, and a report that
    /// <see cref="Normalise"/> refuses are all no report.
    /// </summary>
    /// <param name="json">The stored payload text.</param>
    /// <returns>The normalised report, or null when there is none.</returns>
    public static KubernetesNodePayload? NormaliseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        KubernetesNodePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<KubernetesNodePayload>(json, JsonDefaults.SnakeCase);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        return Normalise(payload);
    }

    /// <summary>
    /// Reports whether a name is a Kubernetes node name: an RFC 1123 subdomain of at most 253
    /// characters, which is lowercase alphanumeric labels joined by dots where a label may hold
    /// hyphens but neither starts nor ends with one. Checked by hand rather than with a regular
    /// expression so that a hostile name can never make the check time out and wedge the projection.
    /// </summary>
    internal static bool IsValidNodeName(string name)
    {
        if ((name.Length == 0) || (name.Length > MaxNodeNameLength))
        {
            return false;
        }

        // Starting as if after a dot makes a leading dot or hyphen fail the same checks as one inside.
        char previous = '.';
        foreach (char character in name)
        {
            if (character == '.')
            {
                if ((previous == '.') || (previous == '-'))
                {
                    return false;
                }
            }
            else if (character == '-')
            {
                if (previous == '.')
                {
                    return false;
                }
            }
            else if (IsLowerAlphanumeric(character) == false)
            {
                return false;
            }

            previous = character;
        }

        return (previous != '.') && (previous != '-');
    }

    private static bool IsLowerAlphanumeric(char character)
    {
        return ((character >= 'a') && (character <= 'z')) || ((character >= '0') && (character <= '9'));
    }

    private static string NormaliseDistro(string? distro)
    {
        if ((distro is not null) && KnownDistros.Contains(distro))
        {
            return distro;
        }

        return UnknownDistro;
    }

    internal static string? NormaliseCaFingerprint(string? fingerprint)
    {
        if ((fingerprint is null) || (fingerprint.Length != CaFingerprintLength))
        {
            return null;
        }

        foreach (char character in fingerprint)
        {
            bool isLowerHex = ((character >= '0') && (character <= '9')) || ((character >= 'a') && (character <= 'f'));
            if (isLowerHex == false)
            {
                return null;
            }
        }

        return fingerprint;
    }

    private static string? NormaliseReporterError(string? reporterError)
    {
        if (reporterError is null)
        {
            return null;
        }

        return KnownReporterErrors.Contains(reporterError) ? reporterError : null;
    }

    internal static string? NormaliseKubeletVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        if (version.Length <= MaxKubeletVersionLength)
        {
            return version;
        }

        // Cutting between the halves of a surrogate pair would leave a character no database encodes.
        int length = MaxKubeletVersionLength;
        if (char.IsHighSurrogate(version[length - 1]))
        {
            length--;
        }

        return version[..length];
    }
}
