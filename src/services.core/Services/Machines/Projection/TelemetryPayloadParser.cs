// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>
/// Pure parsers that turn a telemetry payload into a typed projection fragment.
/// Each parser returns false on malformed JSON — or on structurally valid JSON whose
/// field has the wrong type (which makes the typed accessors throw) — rather than
/// throwing, so a poison row can be skipped without aborting the batch. The JSON
/// property names and the computed-column logic mirror the production projection exactly.
/// </summary>
internal static class TelemetryPayloadParser
{
    /// <summary>Parses a SystemInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw SystemInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseSystemInfo(string payload, out SystemInfoFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new SystemInfoFragment(
                Hostname: ReadString(root, "hostname"),
                HardwareModel: ReadString(root, "hardware_model"),
                IpAddresses: root.TryGetProperty("ip_addresses", out JsonElement ip) ? ip.GetRawText() : null,
                HardwareVendor: ReadString(root, "hardware_vendor"),
                HardwareSerial: ReadString(root, "hardware_serial"),
                CpuBrand: ReadString(root, "cpu_brand"),
                CpuCores: ReadInt(root, "cpu_cores"),
                MemoryTotalBytes: ReadLong(root, "memory_total_bytes"),
                UptimeSeconds: ReadLong(root, "uptime_seconds"),
                BiosVersion: ReadString(root, "bios_version"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses an OsVersion payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw OsVersion telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseOsVersion(string payload, out OsVersionFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new OsVersionFragment(
                OsName: ReadString(root, "os_name"),
                OsVersion: ReadString(root, "os_version"),
                Kernel: ReadString(root, "kernel"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a CpuInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw CpuInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseCpuInfo(string payload, out CpuInfoFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new CpuInfoFragment(
                CpuType: ReadString(root, "cpu_type"),
                CpuPhysicalCpus: ReadInt(root, "physical_cpus"),
                CpuLogicalCpus: ReadInt(root, "logical_cpus"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a MemoryInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw MemoryInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseMemoryInfo(string payload, out MemoryInfoFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new MemoryInfoFragment(
                SwapTotalBytes: ReadLong(root, "swap_total_bytes"),
                SwapFreeBytes: ReadLong(root, "swap_free_bytes"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a DiskInfo payload into a fragment. The raw payload is stored verbatim.</summary>
    /// <param name="payload">The raw DiskInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseDiskInfo(string payload, out DiskInfoFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);

            fragment = new DiskInfoFragment(DiskInfos: payload);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a CpuUsage payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw CpuUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseCpuUsage(string payload, out CpuUsageFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new CpuUsageFragment(CpuUsagePercent: ReadInt(root, "cpu_usage_percent"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a MemoryUsage payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw MemoryUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseMemoryUsage(string payload, out MemoryUsageFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new MemoryUsageFragment(
                MemoryUsagePercent: ReadInt(root, "memory_usage_percent"),
                MemoryUsedBytes: ReadLong(root, "memory_used"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a DiskUsage payload into a fragment, computing the maximum disk usage. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw DiskUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseDiskUsage(string payload, out DiskUsageFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);

            fragment = new DiskUsageFragment(
                MaxDiskUsagePercent: ComputeMaxDiskUsagePercent(payload),
                DiskUsages: payload);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses an SshSessions payload into a fragment. The raw payload is stored verbatim.</summary>
    /// <param name="payload">The raw SshSessions telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseSshSessions(string payload, out SshSessionsFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);

            fragment = new SshSessionsFragment(SshSessions: payload);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a HardwareHealth payload into a fragment, computing the health flags. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw HardwareHealth telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseHardwareHealth(string payload, out HardwareHealthFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);

            (bool hasDiskIssue, bool hasHardwareIssue) = ComputeHardwareHealthFlags(payload);

            fragment = new HardwareHealthFragment(
                HasDiskHealthIssue: hasDiskIssue,
                HasHardwareIssue: hasHardwareIssue,
                HardwareHealth: payload);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a PackageUpdates payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw PackageUpdates telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParsePackageUpdates(string payload, out PackageUpdatesFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new PackageUpdatesFragment(
                PendingUpdates: ReadInt(root, "pending_updates"),
                SecurityUpdates: ReadInt(root, "security_updates"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>Parses a ServiceStatus payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw ServiceStatus telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseServiceStatus(string payload, out ServiceStatusFragment? fragment)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            JsonElement root = doc.RootElement;

            fragment = new ServiceStatusFragment(
                TotalServices: ReadInt(root, "total_services"),
                FailedServices: ReadInt(root, "failed_services"));

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            fragment = null;

            return false;
        }
    }

    /// <summary>
    /// Computes the maximum disk usage percentage across all disks in the JSONB payload.
    /// </summary>
    /// <param name="diskUsagesJson">The raw disk-usage JSON payload.</param>
    /// <returns>The maximum usage percentage, or zero when the payload is malformed or empty.</returns>
    internal static int ComputeMaxDiskUsagePercent(string diskUsagesJson)
    {
        int maxUsage = 0;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(diskUsagesJson);
            JsonElement root = doc.RootElement;

            // The payload is serialized from DiskUtilizationRecord which wraps disks in a "disks" property.
            JsonElement disksElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                disksElement = root;
            }
            else if (root.TryGetProperty("disks", out JsonElement d) && (d.ValueKind == JsonValueKind.Array))
            {
                disksElement = d;
            }
            else
            {
                return maxUsage;
            }

            foreach (JsonElement disk in disksElement.EnumerateArray())
            {
                if (disk.TryGetProperty("usage_percent", out JsonElement up))
                {
                    int usage = up.GetInt32();
                    if (usage > maxUsage)
                    {
                        maxUsage = usage;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Malformed payload, or a wrong-typed field (e.g. a non-numeric usage_percent) — return 0.
        }

        return maxUsage;
    }

    /// <summary>
    /// Computes hardware health flags from the JSONB payload.
    /// Returns (hasDiskHealthIssue, hasHardwareIssue).
    /// </summary>
    /// <param name="hardwareHealthJson">The raw hardware-health JSON payload.</param>
    /// <returns>A tuple indicating whether a disk health issue and/or a hardware issue is present.</returns>
    internal static (bool HasDiskHealthIssue, bool HasHardwareIssue) ComputeHardwareHealthFlags(string hardwareHealthJson)
    {
        bool hasDiskIssue = false;
        bool hasHardwareIssue = false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(hardwareHealthJson);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("disk_smart", out JsonElement diskSmart) &&
                diskSmart.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement disk in diskSmart.EnumerateArray())
                {
                    if (disk.TryGetProperty("health_status", out JsonElement status) &&
                        string.Equals(status.GetString(), "FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDiskIssue = true;

                        break;
                    }
                }
            }

            if (root.TryGetProperty("fans", out JsonElement fans) &&
                fans.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement fan in fans.EnumerateArray())
                {
                    if (fan.TryGetProperty("rpm", out JsonElement rpm) && (rpm.GetInt32() == 0))
                    {
                        hasHardwareIssue = true;

                        break;
                    }
                }
            }

            if ((hasHardwareIssue == false) &&
                root.TryGetProperty("power_supplies", out JsonElement powerSupplies) &&
                powerSupplies.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement ps in powerSupplies.EnumerateArray())
                {
                    if (ps.TryGetProperty("status", out JsonElement psStatus) &&
                        (string.Equals(psStatus.GetString(), "OK", StringComparison.OrdinalIgnoreCase) == false))
                    {
                        hasHardwareIssue = true;

                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Malformed payload, or a wrong-typed field (e.g. a non-numeric rpm) — leave flags as false.
        }

        return (hasDiskIssue, hasHardwareIssue);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetString() : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetInt32() : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetInt64() : null;
}
