// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Startup;

namespace Framlux.FleetManagement.Test.Startup;

/// <summary>
/// M7 tests: <see cref="CorsStartupValidator"/> fails fast on Production CORS misconfiguration
/// (empty origins or wildcard) and permits empty origins in lower environments for dev runs.
/// </summary>
public sealed class CorsStartupValidatorTests
{
    [Test]
    public async Task Validate_Production_EmptyOrigins_Throws()
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            CorsStartupValidator.Validate([], "Production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("Production");
    }

    [Test]
    public async Task Validate_Production_WildcardOrigin_Throws()
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            CorsStartupValidator.Validate(["*"], "Production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("'*'");
    }

    [Test]
    public async Task Validate_Production_WildcardMixedWithValid_Throws()
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            CorsStartupValidator.Validate(["https://app.example.com", "*"], "Production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_Production_ValidOrigins_DoesNotThrow()
    {
        CorsStartupValidator.Validate(["https://app.example.com"], "Production");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Development_EmptyOrigins_DoesNotThrow()
    {
        CorsStartupValidator.Validate([], "Development");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_Production_CaseInsensitive_StillEnforced()
    {
        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            CorsStartupValidator.Validate([], "production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_NullOrigins_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            CorsStartupValidator.Validate(null!, "Production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
