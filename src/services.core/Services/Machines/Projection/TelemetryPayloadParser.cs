// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>
/// Pure parsers that turn a telemetry payload into a typed projection fragment.
/// Each parser returns false on malformed JSON — or on structurally valid JSON whose
/// field has the wrong type (which makes the typed accessors throw) — rather than
/// throwing, so a poison row can be skipped without aborting the batch.
/// </summary>
/// <remarks>
/// Every JSON property name read here is the snake_case form of a field declared on the
/// corresponding message in AgentTelemetry.proto. The ingest path stores each telemetry
/// payload by serializing the received protobuf message with the shared snake_case options,
/// so the proto is the only naming contract: a name that is not on the proto message never
/// appears in a stored payload and would silently project as null.
/// </remarks>
internal static class TelemetryPayloadParser
{
    /// <summary>
    /// The systemd active state that marks a unit as failed. Matches the service history read path
    /// so the projected failed count and the history chart agree on what "failed" means.
    /// </summary>
    private const string FailedServiceActiveState = "failed";

    /// <summary>Parses a SystemInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw SystemInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseSystemInfo(string payload, out SystemInfoFragment? fragment) =>
        TryParse(payload, ParseSystemInfoCore, out fragment);

    /// <summary>Parses an OsVersion payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw OsVersion telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseOsVersion(string payload, out OsVersionFragment? fragment) =>
        TryParse(payload, ParseOsVersionCore, out fragment);

    /// <summary>Parses a CpuInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw CpuInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseCpuInfo(string payload, out CpuInfoFragment? fragment) =>
        TryParse(payload, ParseCpuInfoCore, out fragment);

    /// <summary>Parses a MemoryInfo payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw MemoryInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseMemoryInfo(string payload, out MemoryInfoFragment? fragment) =>
        TryParse(payload, ParseMemoryInfoCore, out fragment);

    /// <summary>Parses a DiskInfo payload into a fragment. The raw payload is stored verbatim.</summary>
    /// <param name="payload">The raw DiskInfo telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseDiskInfo(string payload, out DiskInfoFragment? fragment) =>
        TryParse(payload, _ => ParseDiskInfoCore(payload), out fragment);

    /// <summary>Parses a CpuUsage payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw CpuUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseCpuUsage(string payload, out CpuUsageFragment? fragment) =>
        TryParse(payload, ParseCpuUsageCore, out fragment);

    /// <summary>Parses a MemoryUsage payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw MemoryUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseMemoryUsage(string payload, out MemoryUsageFragment? fragment) =>
        TryParse(payload, ParseMemoryUsageCore, out fragment);

    /// <summary>Parses a DiskUsage payload into a fragment, computing the maximum disk usage. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw DiskUsage telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseDiskUsage(string payload, out DiskUsageFragment? fragment) =>
        TryParse(payload, _ => ParseDiskUsageCore(payload), out fragment);

    /// <summary>Parses an SshSessions payload into a fragment. The raw payload is stored verbatim.</summary>
    /// <param name="payload">The raw SshSessions telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseSshSessions(string payload, out SshSessionsFragment? fragment) =>
        TryParse(payload, _ => ParseSshSessionsCore(payload), out fragment);

    /// <summary>Parses a HardwareHealth payload into a fragment, computing the health flags. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw HardwareHealth telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseHardwareHealth(string payload, out HardwareHealthFragment? fragment) =>
        TryParse(payload, _ => ParseHardwareHealthCore(payload), out fragment);

    /// <summary>Parses a PackageUpdates payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw PackageUpdates telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParsePackageUpdates(string payload, out PackageUpdatesFragment? fragment) =>
        TryParse(payload, ParsePackageUpdatesCore, out fragment);

    /// <summary>Parses a ServiceStatus payload into a fragment. Returns false on malformed JSON.</summary>
    /// <param name="payload">The raw ServiceStatus telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    internal static bool TryParseServiceStatus(string payload, out ServiceStatusFragment? fragment) =>
        TryParse(payload, ParseServiceStatusCore, out fragment);

    /// <summary>
    /// Parses an AgentVersion payload into a fragment. Returns false on malformed JSON, and also
    /// when the payload carries no version: an agent that reports nothing must leave the version
    /// already recorded for the machine in place rather than blank it out.
    /// </summary>
    /// <param name="payload">The raw AgentVersion telemetry payload.</param>
    /// <param name="fragment">The parsed fragment, or null when the payload is malformed or empty.</param>
    /// <returns>True when parsing succeeded and a version was present; otherwise false.</returns>
    internal static bool TryParseAgentVersion(string payload, out AgentVersionFragment? fragment)
    {
        if (TryParse(payload, ParseAgentVersionCore, out fragment) == false)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(fragment?.AgentVersion) == true)
        {
            fragment = null;

            return false;
        }

        return true;
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

    /// <summary>
    /// Parses a payload as JSON and maps its root element into a fragment, treating any
    /// exception raised by malformed JSON or a wrong-typed field the same way: return false
    /// rather than throw, so a poison row can be skipped without aborting the batch.
    /// </summary>
    /// <typeparam name="T">The fragment type produced by <paramref name="parse"/>.</typeparam>
    /// <param name="payload">The raw telemetry payload.</param>
    /// <param name="parse">Maps the parsed JSON root element to a fragment.</param>
    /// <param name="result">The parsed fragment, or the default value when the payload is malformed.</param>
    /// <returns>True when parsing succeeded; otherwise false.</returns>
    private static bool TryParse<T>(string payload, Func<JsonElement, T> parse, out T? result)
    {
        result = default;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            result = parse(document.RootElement);

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Structurally valid JSON whose field has the wrong type (e.g. a string where an int is
            // expected) makes the typed accessors throw InvalidOperationException/FormatException.
            // Treat that the same as malformed JSON: skip the poison row rather than wedge the batch.
            return false;
        }
    }

    private static SystemInfoFragment ParseSystemInfoCore(JsonElement root) =>
        new SystemInfoFragment(
            Hostname: ReadString(root, "hostname"),
            HardwareModel: ReadString(root, "hardware_model"),
            IpAddresses: root.TryGetProperty("ip_addresses", out JsonElement ip) ? ip.GetRawText() : null,
            HardwareVendor: ReadString(root, "hardware_vendor"),
            HardwareSerial: ReadString(root, "hardware_serial"),
            CpuBrand: ReadString(root, "cpu_brand"),
            CpuCores: ReadInt(root, "cpu_physical_cores"),
            MemoryTotalBytes: ReadLong(root, "physical_memory"),
            UptimeSeconds: ReadLong(root, "uptime_seconds"),
            BiosVersion: ReadString(root, "bios_version"));

    private static OsVersionFragment ParseOsVersionCore(JsonElement root) =>
        new OsVersionFragment(
            OsName: ReadString(root, "name"),
            OsVersion: ReadString(root, "version"),
            // OsVersionRecord has no kernel field; the agent reports the output of "uname -r" in build.
            Kernel: ReadString(root, "build"));

    private static CpuInfoFragment ParseCpuInfoCore(JsonElement root) =>
        new CpuInfoFragment(
            CpuType: ReadString(root, "processor_type"),
            CpuPhysicalCpus: ReadNumericText(root, "number_of_cores"),
            CpuLogicalCpus: ReadInt(root, "logical_processors"));

    private static MemoryInfoFragment ParseMemoryInfoCore(JsonElement root) =>
        new MemoryInfoFragment(
            SwapTotalBytes: ReadLong(root, "swap_total"),
            SwapFreeBytes: ReadLong(root, "swap_free"));

    private static DiskInfoFragment ParseDiskInfoCore(string payload) =>
        new DiskInfoFragment(DiskInfos: payload);

    private static CpuUsageFragment ParseCpuUsageCore(JsonElement root) =>
        new CpuUsageFragment(CpuUsagePercent: ReadInt(root, "cpu_usage_percent"));

    private static MemoryUsageFragment ParseMemoryUsageCore(JsonElement root) =>
        new MemoryUsageFragment(
            MemoryUsagePercent: ReadInt(root, "memory_usage_percent"),
            MemoryUsedBytes: ReadLong(root, "memory_used"));

    private static DiskUsageFragment ParseDiskUsageCore(string payload) =>
        new DiskUsageFragment(
            MaxDiskUsagePercent: ComputeMaxDiskUsagePercent(payload),
            DiskUsages: payload);

    private static SshSessionsFragment ParseSshSessionsCore(string payload) =>
        new SshSessionsFragment(SshSessions: payload);

    private static HardwareHealthFragment ParseHardwareHealthCore(string payload)
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = ComputeHardwareHealthFlags(payload);

        return new HardwareHealthFragment(
            HasDiskHealthIssue: hasDiskIssue,
            HasHardwareIssue: hasHardwareIssue,
            HardwareHealth: payload);
    }

    /// <summary>
    /// Derives the pending and security update counts from the reported update list. PackageUpdatesRecord
    /// carries only the list itself, so both counts are computed here rather than read from the payload.
    /// A payload without an updates array is treated as "not reported" so the stored counts survive.
    /// </summary>
    private static PackageUpdatesFragment ParsePackageUpdatesCore(JsonElement root)
    {
        if ((root.TryGetProperty("updates", out JsonElement updates) == false) ||
            (updates.ValueKind != JsonValueKind.Array))
        {
            return new PackageUpdatesFragment(PendingUpdates: null, SecurityUpdates: null);
        }

        int pendingUpdates = 0;
        int securityUpdates = 0;

        foreach (JsonElement update in updates.EnumerateArray())
        {
            pendingUpdates++;

            if (update.TryGetProperty("is_security_update", out JsonElement isSecurityUpdate) &&
                (isSecurityUpdate.ValueKind == JsonValueKind.True))
            {
                securityUpdates++;
            }
        }

        return new PackageUpdatesFragment(PendingUpdates: pendingUpdates, SecurityUpdates: securityUpdates);
    }

    /// <summary>
    /// Derives the total and failed service counts from the reported service list. ServiceStatusRecord
    /// carries only the list itself, so both counts are computed here rather than read from the payload.
    /// A payload without a services array is treated as "not reported" so the stored counts survive.
    /// </summary>
    private static ServiceStatusFragment ParseServiceStatusCore(JsonElement root)
    {
        if ((root.TryGetProperty("services", out JsonElement services) == false) ||
            (services.ValueKind != JsonValueKind.Array))
        {
            return new ServiceStatusFragment(TotalServices: null, FailedServices: null);
        }

        int totalServices = 0;
        int failedServices = 0;

        foreach (JsonElement service in services.EnumerateArray())
        {
            totalServices++;

            if (service.TryGetProperty("active_state", out JsonElement activeState) &&
                string.Equals(activeState.GetString(), FailedServiceActiveState, StringComparison.OrdinalIgnoreCase))
            {
                failedServices++;
            }
        }

        return new ServiceStatusFragment(TotalServices: totalServices, FailedServices: failedServices);
    }

    private static AgentVersionFragment ParseAgentVersionCore(JsonElement root) =>
        new AgentVersionFragment(AgentVersion: ReadString(root, "version"));

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetString() : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetInt32() : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) ? e.GetInt64() : null;

    /// <summary>
    /// Reads a count that the proto declares as text — CpuInfoRecord.number_of_cores is a string —
    /// accepting either a JSON number or a numeric string. Text that is not a number is reported as
    /// absent rather than as a poison row, because the payload still satisfies its declared type.
    /// </summary>
    /// <param name="root">The payload root element.</param>
    /// <param name="name">The JSON property name to read.</param>
    /// <returns>The parsed count, or null when the property is absent or not numeric.</returns>
    private static int? ReadNumericText(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement element) == false)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetInt32();
        }

        if ((element.ValueKind == JsonValueKind.String) &&
            int.TryParse(element.GetString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }
}
