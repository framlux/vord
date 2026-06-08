// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services;

public sealed class AlertConditionStateCleanupJobTests
{
    [Test]
    public async Task RunAsync_DelegatesToRepositoryWithConfiguredRetentionCutoff()
    {
        // Intent: pin the retention window. The production constant is TimeSpan.FromHours(24);
        // a regression to MinValue (delete everything) or to a much larger window (e.g.,
        // TimeSpan.FromDays(7)) must fail this test. Tolerance of one minute accounts for
        // clock drift between the test-side "before" capture and the job's internal UtcNow.
        IAlertConditionStateRepository repo = Substitute.For<IAlertConditionStateRepository>();
        repo.DeleteStaleAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);

        AlertConditionStateCleanupJob job = new(repo, Substitute.For<ILogger<AlertConditionStateCleanupJob>>());

        DateTimeOffset before = DateTimeOffset.UtcNow;
        await job.RunAsync(CancellationToken.None);
        DateTimeOffset expectedCutoff = before - AlertConstants.ConditionStateRetentionWindow;

        await repo.Received(1).DeleteStaleAsync(
            Arg.Is<DateTimeOffset>(d => Math.Abs((expectedCutoff - d).TotalMinutes) < 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RepositoryThrows_ExceptionPropagatesToHangfire()
    {
        // Intent: a DB failure during reaping should not be silently swallowed. Hangfire records
        // it as a failed run and surfaces in the dashboard. The next daily tick retries.
        IAlertConditionStateRepository repo = Substitute.For<IAlertConditionStateRepository>();
        repo.DeleteStaleAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        AlertConditionStateCleanupJob job = new(repo, Substitute.For<ILogger<AlertConditionStateCleanupJob>>());

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(CancellationToken.None));
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).IsEqualTo("DB down");
    }

    [Test]
    public async Task RunAsync_TokenForwardedToRepository()
    {
        // Intent: cancellation must reach the DB call so a worker shutdown does not block on a
        // large delete.
        using CancellationTokenSource cts = new();
        IAlertConditionStateRepository repo = Substitute.For<IAlertConditionStateRepository>();
        repo.DeleteStaleAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);

        AlertConditionStateCleanupJob job = new(repo, Substitute.For<ILogger<AlertConditionStateCleanupJob>>());

        await job.RunAsync(cts.Token);

        await repo.Received(1).DeleteStaleAsync(Arg.Any<DateTimeOffset>(), cts.Token);
    }

    [Test]
    public async Task Constructor_NullArguments_Throw()
    {
        ArgumentNullException? repoEx = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            AlertConditionStateCleanupJob _ = new(null!, Substitute.For<ILogger<AlertConditionStateCleanupJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(repoEx).IsNotNull();
        await Assert.That(repoEx!.ParamName).IsEqualTo("repository");

        ArgumentNullException? loggerEx = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            AlertConditionStateCleanupJob _ = new(Substitute.For<IAlertConditionStateRepository>(), null!);

            return Task.CompletedTask;
        });
        await Assert.That(loggerEx).IsNotNull();
        await Assert.That(loggerEx!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(AlertConditionStateCleanupJob).GetMethod(nameof(AlertConditionStateCleanupJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(AlertConditionStateCleanupJob).GetMethod(nameof(AlertConditionStateCleanupJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(600);
    }

    /// <summary>
    /// M5 invariant: retention window must strictly exceed the validator's max DurationMinutes
    /// so the reaper cannot delete a row mid-window. Raising MaxRuleDurationMinutes without
    /// also raising the safety margin will fail this test.
    /// </summary>
    [Test]
    public async Task RetentionWindow_ExceedsMaxRuleDuration()
    {
        TimeSpan retention = AlertConstants.ConditionStateRetentionWindow;
        TimeSpan maxRule = TimeSpan.FromMinutes(AlertConstants.MaxRuleDurationMinutes);

        await Assert.That(retention > maxRule).IsTrue();
    }
}
