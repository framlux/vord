// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for the classifier that decides whether a hardware identifier is firmware filler rather
/// than something that can tell one machine from another.
/// </summary>
public class HardwareIdentifierPlaceholdersTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("   \t  ")]
    public async Task IsPlaceholder_NullEmptyOrWhitespace_IsPlaceholder(string? value)
    {
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsTrue();
    }

    // Every entry of the placeholder set, spelled the way firmware actually reports it.
    [Test]
    [Arguments("To Be Filled By O.E.M.")]
    [Arguments("To Be Filled By O.E.M")]
    [Arguments("To be filled by O.E.M.")]
    [Arguments("Not Specified")]
    [Arguments("Not Applicable")]
    [Arguments("Not Available")]
    [Arguments("N/A")]
    [Arguments("NA")]
    [Arguments("None")]
    [Arguments("NULL")]
    [Arguments("Unknown")]
    [Arguments("Invalid")]
    [Arguments("Undefined")]
    [Arguments("Empty")]
    [Arguments("Default string")]
    [Arguments("Default String")]
    [Arguments("Default")]
    [Arguments("OEM")]
    [Arguments("O.E.M.")]
    [Arguments("System Serial Number")]
    [Arguments("Chassis Serial Number")]
    [Arguments("Base Board Serial Number")]
    [Arguments("Baseboard Serial Number")]
    [Arguments("Serial Number")]
    [Arguments("No Asset Tag")]
    [Arguments("No Asset Information")]
    [Arguments("Asset Tag")]
    [Arguments("Asset-1234567890")]
    [Arguments("0123456789")]
    [Arguments("123456789")]
    [Arguments("1234567890")]
    [Arguments("12345678")]
    public async Task IsPlaceholder_KnownFirmwareFiller_IsPlaceholder(string value)
    {
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsTrue();
    }

    [Test]
    [Arguments("system serial number")]
    [Arguments("SYSTEM SERIAL NUMBER")]
    [Arguments("System   Serial   Number")]
    [Arguments("  System Serial Number  ")]
    [Arguments("System-Serial-Number")]
    [Arguments("System_Serial_Number")]
    [Arguments("SystemSerialNumber")]
    [Arguments("System.Serial.Number")]
    [Arguments("System/Serial/Number")]
    [Arguments("n/a")]
    [Arguments("N.A.")]
    [Arguments("N-A")]
    [Arguments("TO BE FILLED BY OEM")]
    [Arguments("to-be-filled-by-o.e.m.")]
    [Arguments("DEFAULT STRING")]
    [Arguments("default_string")]
    [Arguments("asset-1234567890")]
    [Arguments("ASSET 1234567890")]
    [Arguments("0123-456-789")]
    public async Task IsPlaceholder_CaseSpacingAndPunctuationVariants_IsPlaceholder(string value)
    {
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsTrue();
    }

    [Test]
    [Arguments("0")]
    [Arguments("00")]
    [Arguments("00000000")]
    [Arguments("0000000000000000")]
    [Arguments("FFFFFFFF")]
    [Arguments("ffffffff")]
    [Arguments("xxxxxxxx")]
    [Arguments("XXXXXXXXXXXX")]
    [Arguments("11111111")]
    [Arguments("0000-0000-0000")]
    [Arguments("00 00 00 00")]
    [Arguments("a")]
    [Arguments("...")]
    [Arguments("---")]
    [Arguments("_ _ _")]
    [Arguments("//")]
    public async Task IsPlaceholder_DegeneratePatterns_IsPlaceholder(string value)
    {
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsTrue();
    }

    [Test]
    [Arguments("C02XL0GTJGH5")]
    [Arguments("PF2ABCDE")]
    [Arguments("mid-0123abcd4567")]
    [Arguments("gen-6f1c2a90-3b7d-4e5f-8a1b-9c0d2e3f4a5b")]
    [Arguments("VMware-56 4d 12 34 56 78 9a bc-de f0 12 34 56 78 9a bc")]
    [Arguments("Serial123")]
    [Arguments("MXL1234ABC")]
    [Arguments("SN-TEST")]
    [Arguments("ASSET-00417")]
    [Arguments("20240115-0001")]
    public async Task IsPlaceholder_RealIdentifier_IsNotPlaceholder(string value)
    {
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsFalse();
    }

    [Test]
    [Arguments("My Serial Number Is X1")]
    [Arguments("Serial Number 12345")]
    [Arguments("Defaulted-7731")]
    [Arguments("None-4471")]
    [Arguments("Unknown Board 9")]
    [Arguments("1234567891")]
    [Arguments("123456")]
    [Arguments("0123456780")]
    public async Task IsPlaceholder_ContainsFillerWordButIsMore_IsNotPlaceholder(string value)
    {
        // Matching is exact on the normalised value, never by substring: a genuine identifier may
        // well contain the word "serial" or "none".
        await Assert.That(HardwareIdentifierPlaceholders.IsPlaceholder(value)).IsFalse();
    }
}
