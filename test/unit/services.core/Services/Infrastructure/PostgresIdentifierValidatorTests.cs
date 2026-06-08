// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// H5 tests: locks down the allowlist contract on
/// <see cref="PostgresIdentifierValidator"/> so future regressions cannot widen the
/// accepted character set without explicit changes here.
/// </summary>
public sealed class PostgresIdentifierValidatorTests
{
    [Test]
    public async Task Validate_PlainAlpha_DoesNotThrow()
    {
        PostgresIdentifierValidator.Validate("Telemetry", "tableName");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_AlphaNumeric_DoesNotThrow()
    {
        PostgresIdentifierValidator.Validate("MachineStateLog42", "tableName");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_UnderscoreLeading_DoesNotThrow()
    {
        PostgresIdentifierValidator.Validate("_internal_table", "tableName");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_AtMaxLength63_DoesNotThrow()
    {
        string sixtyThree = new('a', 63);

        PostgresIdentifierValidator.Validate(sixtyThree, "tableName");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_OverMaxLength64_Throws()
    {
        string sixtyFour = new('a', 64);

        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate(sixtyFour, "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_LeadingDigit_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate("1bad", "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("tableName");
    }

    [Test]
    public async Task Validate_SqlInjectionAttempt_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate("Foo'); DROP TABLE Tenants; --", "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_SpaceCharacter_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate("has space", "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_QuotationMark_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate("foo\"bar", "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_EmptyString_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate(string.Empty, "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_NullIdentifier_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PostgresIdentifierValidator.Validate(null!, "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task Validate_NonAsciiUnicodeChar_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PostgresIdentifierValidator.Validate("Téléemétry", "tableName");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
