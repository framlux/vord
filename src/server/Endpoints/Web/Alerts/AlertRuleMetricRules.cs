// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Alerts;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Single source of truth for the metric-dependent threshold and duration constraints that apply to
/// alert rules. Shared by the create and update request validators and by the update endpoint's
/// database-loaded re-validation so the three sites cannot drift apart on which metrics are
/// percentage- or binary-valued, on the bounds, or on the messages surfaced to the caller.
/// </summary>
internal static class AlertRuleMetricRules
{
    /// <summary>
    /// Determines whether the metric is expressed as a percentage (0–100), e.g. CPU, memory, or disk usage.
    /// </summary>
    internal static bool IsPercentageMetric(AlertMetric metric)
    {
        return metric is AlertMetric.CpuUsage or AlertMetric.MemoryUsage or AlertMetric.DiskUsage;
    }

    /// <summary>
    /// Determines whether the metric is binary (only 0 or 1 is meaningful), e.g. machine offline,
    /// disk health or stale telemetry.
    /// </summary>
    internal static bool IsBinaryMetric(AlertMetric metric)
    {
        return metric is AlertMetric.MachineOffline or AlertMetric.DiskHealth or AlertMetric.TelemetryStale;
    }

    /// <summary>
    /// Validates the threshold against the bounds implied by the metric type: percentage metrics require
    /// 0–100, binary metrics require exactly 0 or 1, and all other metrics require a non-negative value.
    /// </summary>
    internal static bool ValidateThresholdForMetric(AlertMetric metric, decimal threshold)
    {
        if (IsPercentageMetric(metric))
        {
            return (threshold >= 0) && (threshold <= 100);
        }

        if (IsBinaryMetric(metric))
        {
            return (threshold == 0) || (threshold == 1);
        }

        return threshold >= 0;
    }

    /// <summary>
    /// Returns the validation message describing the threshold bounds for the metric type.
    /// </summary>
    internal static string GetThresholdValidationMessage(AlertMetric metric)
    {
        if (IsPercentageMetric(metric))
        {
            return "Threshold for percentage metrics must be between 0 and 100";
        }

        if (IsBinaryMetric(metric))
        {
            return "Threshold for this metric must be 0 or 1";
        }

        return "Threshold must be zero or positive";
    }

    /// <summary>
    /// Validates the operator against the metric type. Most metrics accept any comparison; a windowed
    /// count of failed SSH logins accepts only <see cref="AlertOperator.GreaterThan"/>.
    /// </summary>
    /// <remarks>
    /// A brute-force run takes the count from four to dozens between two windows, so an equality test
    /// lands on the threshold only by chance and misses the incident it exists to catch. A less-than
    /// test is worse: every ordinary window contains a mistyped password or two, so it would alert on
    /// the fleet behaving normally.
    /// </remarks>
    internal static bool ValidateOperatorForMetric(AlertMetric metric, AlertOperator op)
    {
        if (metric == AlertMetric.FailedSshLogin)
        {
            return op == AlertOperator.GreaterThan;
        }

        return true;
    }

    /// <summary>
    /// Returns the validation message describing the operators the metric accepts.
    /// </summary>
    internal static string GetOperatorValidationMessage(AlertMetric metric)
    {
        if (metric == AlertMetric.FailedSshLogin)
        {
            return "Failed SSH login alerts must use the 'more than' operator";
        }

        return "Operator is not valid for this metric";
    }

    /// <summary>
    /// Validates the duration against the metric type: point-in-time metrics require exactly zero, and
    /// every other metric requires a value between its per-metric minimum and the global maximum rule
    /// duration. Keyed on <see cref="AlertConstants.RequiresZeroDuration"/> rather than
    /// <see cref="AlertConstants.IsEventMetric"/> — a metric can be evaluated at ingest and still
    /// measure a window, and such a rule needs a real duration.
    /// </summary>
    internal static bool ValidateDurationForMetric(AlertMetric metric, int duration)
    {
        if (AlertConstants.RequiresZeroDuration(metric))
        {
            return duration == 0;
        }

        return (duration >= AlertConstants.GetMinimumDurationMinutes(metric)) &&
               (duration <= AlertConstants.MaxRuleDurationMinutes);
    }

    /// <summary>
    /// Returns the validation message describing the duration bounds for the metric type.
    /// </summary>
    internal static string GetDurationValidationMessage(AlertMetric metric)
    {
        if (AlertConstants.RequiresZeroDuration(metric))
        {
            return "Duration must be zero for point-in-time metrics";
        }

        int minimum = AlertConstants.GetMinimumDurationMinutes(metric);

        return $"Duration for {metric} alerts must be between {minimum} and {AlertConstants.MaxRuleDurationMinutes} minutes";
    }

    /// <summary>
    /// Validates the threshold for a metric supplied as a request string. An unparseable metric defers to the
    /// non-negative rule so the dedicated "invalid metric" rule is the one that surfaces the parse failure.
    /// </summary>
    internal static bool ValidateThresholdForMetric(string? metric, decimal threshold)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return threshold >= 0;
        }

        return ValidateThresholdForMetric(parsed, threshold);
    }

    /// <summary>
    /// Returns the threshold validation message for a metric supplied as a request string.
    /// </summary>
    internal static string GetThresholdValidationMessage(string? metric)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return "Threshold must be zero or positive";
        }

        return GetThresholdValidationMessage(parsed);
    }

    /// <summary>
    /// Validates the duration for a metric supplied as a request string. An unparseable metric defers to the
    /// non-negative rule so the dedicated "invalid metric" rule is the one that surfaces the parse failure.
    /// </summary>
    internal static bool ValidateDurationForMetric(string? metric, int duration)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return duration >= 0;
        }

        return ValidateDurationForMetric(parsed, duration);
    }

    /// <summary>
    /// Returns the duration validation message for a metric supplied as a request string.
    /// </summary>
    internal static string GetDurationValidationMessage(string? metric)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return "Duration must be zero or positive";
        }

        return GetDurationValidationMessage(parsed);
    }

    /// <summary>
    /// Validates the operator for a metric and operator supplied as request strings. An unparseable
    /// metric or operator passes here so the dedicated "invalid metric" and "invalid operator" rules
    /// are the ones that surface the parse failure.
    /// </summary>
    internal static bool ValidateOperatorForMetric(string? metric, string? op)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsedMetric) == false)
        {
            return true;
        }

        if (Enum.TryParse<AlertOperator>(op, true, out AlertOperator parsedOperator) == false)
        {
            return true;
        }

        return ValidateOperatorForMetric(parsedMetric, parsedOperator);
    }

    /// <summary>
    /// Returns the operator validation message for a metric supplied as a request string.
    /// </summary>
    internal static string GetOperatorValidationMessage(string? metric)
    {
        if (Enum.TryParse<AlertMetric>(metric, true, out AlertMetric parsed) == false)
        {
            return "Operator is not valid for this metric";
        }

        return GetOperatorValidationMessage(parsed);
    }
}
