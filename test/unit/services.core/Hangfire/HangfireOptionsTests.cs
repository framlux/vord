// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using Framlux.FleetManagement.Services.Core.Hangfire;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// Tests for <see cref="HangfireOptions"/> data-annotation validation. Intent: a blank schema
/// name or zero worker count must fail validation at startup rather than booting silently with
/// no jobs running.
/// </summary>
public sealed class HangfireOptionsTests
{
    [Test]
    public async Task Defaults_AreValid()
    {
        HangfireOptions opts = new();
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsTrue();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task BlankSchemaName_FailsValidation()
    {
        HangfireOptions opts = new() { SchemaName = string.Empty };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(HangfireOptions.SchemaName)))).IsTrue();
    }

    [Test]
    public async Task WorkerCountZero_FailsValidation()
    {
        HangfireOptions opts = new() { WorkerCount = 0 };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(HangfireOptions.WorkerCount)))).IsTrue();
    }

    // ==========================================================================================
    // M12 regression: InvisibilityTimeoutMinutes default and range validation.
    // ==========================================================================================

    [Test]
    public async Task InvisibilityTimeoutMinutes_AboveMax_FailsValidation()
    {
        HangfireOptions opts = new() { InvisibilityTimeoutMinutes = 1441 };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(HangfireOptions.InvisibilityTimeoutMinutes)))).IsTrue();
    }

    [Test]
    public async Task InvisibilityTimeoutMinutes_BelowMin_FailsValidation()
    {
        HangfireOptions opts = new() { InvisibilityTimeoutMinutes = 0 };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(HangfireOptions.InvisibilityTimeoutMinutes)))).IsTrue();
    }

    [Test]
    public async Task InvisibilityTimeoutMinutes_AtMaxBoundary_PassesValidation()
    {
        HangfireOptions opts = new() { InvisibilityTimeoutMinutes = 1440 };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task InvisibilityTimeoutMinutes_AtMinBoundary_PassesValidation()
    {
        HangfireOptions opts = new() { InvisibilityTimeoutMinutes = 1 };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsTrue();
    }
}



