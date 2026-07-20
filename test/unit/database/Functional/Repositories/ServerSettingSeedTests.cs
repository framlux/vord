// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Guards the migration's frozen server-setting seed rows against drift. Runs the real
/// migrations on a standalone database and reads the seeded rows back, proving that every
/// defined setting key is seeded exactly once (adding an enum member without a seed row fails
/// here, as does a leftover row for a removed member), that each seeded value matches the
/// runtime default the read paths fall back to, and that each value passes admin validation.
/// </summary>
public sealed class ServerSettingSeedTests
{
    private static readonly IReadOnlyDictionary<ServerConfigurationSettingKeys, string> ExpectedDefaults =
        new Dictionary<ServerConfigurationSettingKeys, string>
        {
            [ServerConfigurationSettingKeys.AgentHeartbeatSeconds] = ServerSettingDefaults.AgentHeartbeatSeconds.ToString(),
            [ServerConfigurationSettingKeys.AgentConfigRefreshSeconds] = ServerSettingDefaults.AgentConfigRefreshSeconds.ToString(),
            [ServerConfigurationSettingKeys.OnlineThresholdSeconds] = ServerSettingDefaults.OnlineThresholdSeconds.ToString(),
            [ServerConfigurationSettingKeys.DeduplicationTtlSeconds] = ServerSettingDefaults.DeduplicationTtlSeconds.ToString(),
            [ServerConfigurationSettingKeys.AgentCommandPollSeconds] = ServerSettingDefaults.AgentCommandPollSeconds.ToString(),
            [ServerConfigurationSettingKeys.AllowUserSignup] = ServerSettingDefaults.AllowUserSignup ? "true" : "false",
            [ServerConfigurationSettingKeys.TelemetryCollectFastSeconds] = ServerSettingDefaults.TelemetryCollectFastSeconds.ToString(),
            [ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds] = ServerSettingDefaults.TelemetryCollectSlowSeconds.ToString(),
            [ServerConfigurationSettingKeys.TelemetrySendFastSeconds] = ServerSettingDefaults.TelemetrySendFastSeconds.ToString(),
            [ServerConfigurationSettingKeys.TelemetrySendSlowSeconds] = ServerSettingDefaults.TelemetrySendSlowSeconds.ToString(),
            [ServerConfigurationSettingKeys.ServiceStatusSeconds] = ServerSettingDefaults.ServiceStatusSeconds.ToString(),
        };

    private static Dictionary<ServerConfigurationSettingKeys, string> ReadSeededSettings()
    {
        string dbFile = Path.Combine(Path.GetTempPath(), $"seedtest_{Guid.NewGuid():N}.db");
        try
        {
            TestDatabaseFactory.ApplyMigrations(dbFile);

            Dictionary<ServerConfigurationSettingKeys, string> rows = [];
            using SqliteConnection connection = new($"Data Source={dbFile};Mode=ReadOnly");
            connection.Open();
            using SqliteCommand cmd = connection.CreateCommand();
            // Physical table name per TableNames.ServerConfigurationSettings (internal constant).
            cmd.CommandText = "SELECT \"Key\", \"Value\" FROM \"ConfigurationSettings\"";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((ServerConfigurationSettingKeys)reader.GetInt32(0), reader.GetString(1));
            }

            return rows;
        }
        finally
        {
            try { File.Delete(dbFile); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Migration_SeedsExactlyTheDefinedSettingKeys()
    {
        Dictionary<ServerConfigurationSettingKeys, string> seeded = ReadSeededSettings();

        List<ServerConfigurationSettingKeys> expectedKeys = Enum.GetValues<ServerConfigurationSettingKeys>()
            .Where(k => k != ServerConfigurationSettingKeys.None)
            .OrderBy(k => (int)k)
            .ToList();

        List<ServerConfigurationSettingKeys> seededKeys = seeded.Keys
            .OrderBy(k => (int)k)
            .ToList();

        await Assert.That(seededKeys).IsEquivalentTo(expectedKeys);
    }

    [Test]
    public async Task Migration_SeededValues_MatchRuntimeDefaults()
    {
        Dictionary<ServerConfigurationSettingKeys, string> seeded = ReadSeededSettings();

        foreach (KeyValuePair<ServerConfigurationSettingKeys, string> row in seeded)
        {
            await Assert.That(ExpectedDefaults.ContainsKey(row.Key)).IsTrue();
            await Assert.That(row.Value).IsEqualTo(ExpectedDefaults[row.Key]);
        }
    }

    [Test]
    public async Task Migration_SeededValues_AllPassAdminValidation()
    {
        Dictionary<ServerConfigurationSettingKeys, string> seeded = ReadSeededSettings();

        await Assert.That(seeded.Count).IsGreaterThan(0);

        foreach (KeyValuePair<ServerConfigurationSettingKeys, string> row in seeded)
        {
            string? validationError = ServerSettingValidation.Validate(row.Key, row.Value);

            await Assert.That(validationError).IsNull();
        }
    }
}
