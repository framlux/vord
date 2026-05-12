// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentValidation.Results;
using Framlux.FleetManagement.Services.Core.Models.Tenants;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Tenants;

/// <summary>
/// Unit tests for <see cref="UpdateTenantOidcConfigValidator"/>.
/// </summary>
public sealed class UpdateTenantOidcConfigValidatorTests
{
    private readonly UpdateTenantOidcConfigValidator _validator = new();

    private static TenantOidcConfigDto ValidRequest()
    {
        return new TenantOidcConfigDto
        {
            Authority = "https://login.example.com",
            ClientId = "client-id-123",
            ClientSecret = "super-secret-value",
            EmailDomain = "example.com",
            IsEnabled = true
        };
    }

    [Test]
    public async Task ValidRequest_PassesValidation()
    {
        ValidationResult result = await _validator.ValidateAsync(ValidRequest());

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task ValidRequestWithMetadataAddress_PassesValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.MetadataAddress = "https://login.example.com/.well-known/openid-configuration";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    // ================================================================
    // Authority validation
    // ================================================================

    [Test]
    public async Task EmptyAuthority_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.Authority = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Authority URL is required")).IsTrue();
    }

    [Test]
    public async Task HttpAuthority_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.Authority = "http://login.example.com";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Authority URL must use HTTPS")).IsTrue();
    }

    // ================================================================
    // ClientId validation
    // ================================================================

    [Test]
    public async Task EmptyClientId_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.ClientId = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Client ID is required")).IsTrue();
    }

    // ================================================================
    // ClientSecret validation
    // ================================================================

    [Test]
    public async Task EmptyClientSecret_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.ClientSecret = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Client secret is required")).IsTrue();
    }

    // ================================================================
    // EmailDomain validation
    // ================================================================

    [Test]
    public async Task EmptyEmailDomain_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.EmailDomain = string.Empty;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Email domain is required")).IsTrue();
    }

    // ================================================================
    // MetadataAddress validation
    // ================================================================

    [Test]
    public async Task NullMetadataAddress_PassesValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.MetadataAddress = null;

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsTrue();
    }

    [Test]
    public async Task HttpMetadataAddress_FailsValidation()
    {
        TenantOidcConfigDto request = ValidRequest();
        request.MetadataAddress = "http://login.example.com/.well-known";

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Any(e => e.ErrorMessage == "Metadata address must use HTTPS")).IsTrue();
    }

    // ================================================================
    // Multiple errors
    // ================================================================

    [Test]
    public async Task AllFieldsEmpty_ReportsMultipleErrors()
    {
        TenantOidcConfigDto request = new()
        {
            Authority = string.Empty,
            ClientId = string.Empty,
            ClientSecret = string.Empty,
            EmailDomain = string.Empty,
        };

        ValidationResult result = await _validator.ValidateAsync(request);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Errors.Count).IsGreaterThanOrEqualTo(4);
    }
}
