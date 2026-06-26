// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using Framlux.FleetManagement.Services.Core.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="AppOptions"/> data-annotation validation. Intent: a missing or malformed
/// BaseUrl must fail validation at startup rather than booting silently and shipping broken
/// relative links in alert and invitation emails.
/// </summary>
public sealed class AppOptionsTests
{
    [Test]
    public async Task ValidAbsoluteUrl_PassesValidation()
    {
        AppOptions opts = new() { BaseUrl = "https://app.vordfleet.dev" };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsTrue();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task EmptyBaseUrl_FailsValidation()
    {
        AppOptions opts = new() { BaseUrl = string.Empty };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(AppOptions.BaseUrl)))).IsTrue();
    }

    [Test]
    public async Task MalformedBaseUrl_FailsValidation()
    {
        AppOptions opts = new() { BaseUrl = "not-a-url" };
        List<ValidationResult> errors = new();
        bool ok = Validator.TryValidateObject(opts, new ValidationContext(opts), errors, validateAllProperties: true);

        await Assert.That(ok).IsFalse();
        await Assert.That(errors.Any(e => e.MemberNames.Contains(nameof(AppOptions.BaseUrl)))).IsTrue();
    }
}
