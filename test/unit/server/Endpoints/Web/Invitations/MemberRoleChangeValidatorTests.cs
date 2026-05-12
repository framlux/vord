// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Invitations;

/// <summary>
/// Unit tests for <see cref="MemberRoleChangeValidator"/>.
/// </summary>
public sealed class MemberRoleChangeValidatorTests
{
    private readonly MemberRoleChangeValidator _validator = new();

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ZeroUserId_FailsValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 0, Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "User ID must be greater than zero")).IsTrue();
    }

    [Test]
    public async Task NegativeUserId_FailsValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = -1, Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "User ID must be greater than zero")).IsTrue();
    }

    [Test]
    public async Task EmptyRole_FailsValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = string.Empty };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Role is required")).IsTrue();
    }

    [Test]
    public async Task InvalidRole_FailsValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = "SuperAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Role must be one of: TenantAdmin, MachineAdmin, Viewer")).IsTrue();
    }

    [Test]
    public async Task TenantAdminRole_PassesValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = "TenantAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task MachineAdminRole_PassesValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = "MachineAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task NoneRole_FailsValidation()
    {
        MemberRoleChangeRequest request = new() { UserId = 5, Role = "None" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
    }
}
