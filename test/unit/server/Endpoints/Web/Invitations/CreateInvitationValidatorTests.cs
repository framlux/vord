// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Invitations;

/// <summary>
/// Unit tests for <see cref="CreateInvitationValidator"/>.
/// </summary>
public sealed class CreateInvitationValidatorTests
{
    private readonly CreateInvitationValidator _validator = new();

    [Test]
    public async Task ValidRequest_WithRole_PassesValidation()
    {
        CreateInvitationRequest request = new() { Email = "user@example.com", Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ValidRequest_WithoutRole_PassesValidation()
    {
        CreateInvitationRequest request = new() { Email = "user@example.com", Role = null };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task EmptyEmail_FailsValidation()
    {
        CreateInvitationRequest request = new() { Email = string.Empty, Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Email is required")).IsTrue();
    }

    [Test]
    public async Task InvalidEmailFormat_FailsValidation()
    {
        CreateInvitationRequest request = new() { Email = "not-an-email", Role = "Viewer" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "A valid email address is required")).IsTrue();
    }

    [Test]
    public async Task TenantAdminRole_PassesValidation()
    {
        CreateInvitationRequest request = new() { Email = "admin@example.com", Role = "TenantAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task MachineAdminRole_PassesValidation()
    {
        CreateInvitationRequest request = new() { Email = "admin@example.com", Role = "MachineAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task InvalidRole_FailsValidation()
    {
        CreateInvitationRequest request = new() { Email = "user@example.com", Role = "SuperAdmin" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Role must be one of: TenantAdmin, MachineAdmin, Viewer")).IsTrue();
    }

    [Test]
    public async Task NoneRole_FailsValidation()
    {
        CreateInvitationRequest request = new() { Email = "user@example.com", Role = "None" };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Role must be one of: TenantAdmin, MachineAdmin, Viewer")).IsTrue();
    }
}
