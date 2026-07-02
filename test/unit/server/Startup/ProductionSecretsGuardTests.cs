// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Startup;

namespace Framlux.FleetManagement.Test.Startup;

/// <summary>
/// Verifies the production secrets guard refuses to start with empty or placeholder database or
/// Redis secrets in Production, while remaining a no-op in lower environments.
/// </summary>
public sealed class ProductionSecretsGuardTests
{
    private const string RealSecret = "a-real-strong-secret";

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("CHANGE_ME")]
    [Arguments("changeme")]
    [Arguments("password")]
    [Arguments("REPLACE_ME")]
    public async Task Validate_Production_MissingOrPlaceholderDatabasePassword_Throws(string? password)
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ProductionSecretsGuard.Validate("Production", password, RealSecret);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Database");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("CHANGE_ME")]
    [Arguments("changeme")]
    [Arguments("password")]
    [Arguments("REPLACE_ME")]
    public async Task Validate_Production_MissingOrPlaceholderRedisPassword_Throws(string? password)
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ProductionSecretsGuard.Validate("Production", RealSecret, password);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Redis");
    }

    [Test]
    public async Task Validate_Production_RealSecrets_DoesNotThrow()
    {
        await Assert.That(() =>
        {
            ProductionSecretsGuard.Validate("Production", RealSecret, "a-real-redis-secret");

            return Task.CompletedTask;
        }).ThrowsNothing();
    }

    [Test]
    [Arguments("Development")]
    [Arguments("Staging")]
    [Arguments("Test")]
    public async Task Validate_NonProduction_PlaceholderSecrets_DoesNotThrow(string environment)
    {
        await Assert.That(() =>
        {
            ProductionSecretsGuard.Validate(environment, "CHANGE_ME", "CHANGE_ME");

            return Task.CompletedTask;
        }).ThrowsNothing();
    }

    [Test]
    public async Task Validate_NullEnvironment_Throws()
    {
        await Assert.That(() =>
        {
            ProductionSecretsGuard.Validate(null!, RealSecret, RealSecret);

            return Task.CompletedTask;
        }).Throws<ArgumentNullException>();
    }
}
