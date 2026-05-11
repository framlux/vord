// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Tenants;

/// <summary>
/// Unit tests for <see cref="TenantSwitchValidator"/>.
/// </summary>
public sealed class TenantSwitchValidatorTests
{
    private readonly TenantSwitchValidator _validator = new();

    [Test]
    public async Task ValidTenantId_PassesValidation()
    {
        TenantSwitchRequest request = new() { TenantId = 1 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ZeroTenantId_FailsValidation()
    {
        TenantSwitchRequest request = new() { TenantId = 0 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Tenant ID must be a positive integer")).IsTrue();
    }

    [Test]
    public async Task NegativeTenantId_FailsValidation()
    {
        TenantSwitchRequest request = new() { TenantId = -1 };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }
}
