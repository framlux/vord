// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Gives every already-provisioned tenant the stale-telemetry built-in alert rule. Built-in rules
/// are seeded at tenant creation and, as a backstop, on entitled tier transitions; nothing
/// reconciles them on a schedule, so a tenant created before this rule existed would otherwise never
/// receive it. That matters because a machine whose agent still answers while its telemetry is
/// wedged used to reach Offline and fire the Critical "Machine offline" rule — it now reads Online
/// and Warning instead, and without this rule nothing would page anyone for it again.
/// </summary>
[MigrationVersion(2026, 09, 13, 2)]
public sealed class AddTelemetryStaleBuiltInAlertRule : Migration
{
    /// <summary>
    /// Inserts the rule for every tenant that already holds the built-in offline rule and does not
    /// yet hold this one. Exposed so a test can replay the exact shipped statement rather than a
    /// copy of it: a migration runs once, and on a fresh database it runs before any tenant exists.
    /// </summary>
    public static string BackfillSql { get; } = BuildBackfillSql("true", "false");

    /// <inheritdoc/>
    public override void Up()
    {
        IfDatabase("PostgreSQL").Execute.Sql(BackfillSql);
        IfDatabase("SQLite").Execute.Sql(BuildBackfillSql("1", "0"));
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql(BuildRemoveSql("false"));
        IfDatabase("SQLite").Execute.Sql(BuildRemoveSql("0"));
    }

    /// <summary>
    /// Builds the backfill statement for a dialect's boolean literals. One definition for both
    /// dialects, because two hand-written transcriptions of the same insert are two things to keep
    /// in step.
    /// </summary>
    /// <param name="trueLiteral">The dialect's literal for true.</param>
    /// <param name="falseLiteral">The dialect's literal for false.</param>
    /// <remarks>
    /// The enum values are pinned as literals — metric 4 is <c>MachineOffline</c>, metric 9 is
    /// <c>TelemetryStale</c>, operator 3 is <c>EqualTo</c> and severity 2 is <c>Warning</c> —
    /// because a future enum edit must not retroactively change what a shipped migration did.
    /// <para>
    /// The tenant's existing offline rule is the template for the three values that are not fixed by
    /// the definition. <c>IsEnabled</c> is copied because built-in rules are seeded off for an
    /// unentitled tenant and turned on by the tier transition; backfilling enabled would give a Free
    /// tenant a rule it never chose and that nothing would later turn off. <c>CreatedByUserId</c> is
    /// copied because the column is a real foreign key. The timestamps are copied rather than read
    /// from a clock so the stored format matches exactly what the application writes on each
    /// database.
    /// </para>
    /// <para>
    /// Keying on the offline rule also scopes the backfill to tenants that were actually
    /// provisioned: a tenant holding no built-in rules at all has never been seeded, and
    /// provisioning writes it the whole set on its next entitled transition.
    /// </para>
    /// </remarks>
    private static string BuildBackfillSql(string trueLiteral, string falseLiteral)
    {
        return $"""
            INSERT INTO "{TableNames.AlertRules}"
                ("TenantId", "Name", "Metric", "Operator", "Threshold", "DurationMinutes", "Severity",
                 "IsEnabled", "NotifyEmail", "NotifyWebhook", "IsCustom", "CreatedByUserId",
                 "CreatedAt", "UpdatedAt")
            SELECT offline."TenantId", 'Telemetry stopped', 9, 3, 1, 10, 2,
                   offline."IsEnabled", {trueLiteral}, {falseLiteral}, {falseLiteral},
                   offline."CreatedByUserId", offline."CreatedAt", offline."UpdatedAt"
            FROM "{TableNames.AlertRules}" offline
            WHERE offline."Metric" = 4
              AND offline."IsCustom" = {falseLiteral}
              AND NOT EXISTS (
                  SELECT 1 FROM "{TableNames.AlertRules}" existing
                  WHERE existing."TenantId" = offline."TenantId"
                    AND existing."Metric" = 9
                    AND existing."IsCustom" = {falseLiteral})
            """;
    }

    /// <summary>
    /// Builds the statement removing the backfilled rule for a dialect's boolean literals. Custom
    /// rules on the same metric are a tenant's own work and are left alone.
    /// </summary>
    /// <param name="falseLiteral">The dialect's literal for false.</param>
    private static string BuildRemoveSql(string falseLiteral)
    {
        return $"""
            DELETE FROM "{TableNames.AlertRules}"
            WHERE "Metric" = 9 AND "IsCustom" = {falseLiteral}
            """;
    }
}
