// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines.Projection;

namespace Framlux.FleetManagement.Test.Services.Machines.Projection;

/// <summary>
/// Tests for <see cref="MachineStateColumnLimits"/>: the cut that keeps agent-reported text inside the
/// bounded columns the projection writes.
/// </summary>
public sealed class MachineStateColumnLimitsTests
{
    private const string Emoji = "\U0001F600";

    [Test]
    public async Task Clamp_Null_StaysNull()
    {
        await Assert.That(MachineStateColumnLimits.Clamp(null, 255)).IsNull();
    }

    [Test]
    public async Task Clamp_Empty_StaysEmpty()
    {
        await Assert.That(MachineStateColumnLimits.Clamp(string.Empty, 255)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Clamp_ShorterThanTheLimit_IsUnchanged()
    {
        await Assert.That(MachineStateColumnLimits.Clamp("Raspberry Pi 4 Model B Rev 1.4", 255)).IsEqualTo("Raspberry Pi 4 Model B Rev 1.4");
    }

    [Test]
    public async Task Clamp_ExactlyTheLimit_IsUnchanged()
    {
        string value = new('x', 255);

        await Assert.That(MachineStateColumnLimits.Clamp(value, 255)).IsEqualTo(value);
    }

    [Test]
    public async Task Clamp_OneOverTheLimit_IsCutToTheLimit()
    {
        await Assert.That(MachineStateColumnLimits.Clamp(new string('x', 256), 255)).IsEqualTo(new string('x', 255));
    }

    [Test]
    public async Task Clamp_FarOverTheLimit_IsCutToTheLimit()
    {
        await Assert.That(MachineStateColumnLimits.Clamp(new string('x', 100_000), 64)).IsEqualTo(new string('x', 64));
    }

    [Test]
    public async Task Clamp_SurrogatePairStraddlingTheCut_IsDroppedWhole()
    {
        string value = new string('x', 254) + Emoji;

        await Assert.That(MachineStateColumnLimits.Clamp(value, 255)).IsEqualTo(new string('x', 254));
    }

    [Test]
    public async Task Clamp_SurrogatePairEndingExactlyAtTheLimit_IsKept()
    {
        string value = new string('x', 253) + Emoji + "tail";

        await Assert.That(MachineStateColumnLimits.Clamp(value, 255)).IsEqualTo(new string('x', 253) + Emoji);
    }

    [Test]
    public async Task Clamp_CutResultNeverEndsInALoneHighSurrogate()
    {
        for (int length = 1; length <= 8; length++)
        {
            string cut = MachineStateColumnLimits.Clamp(string.Concat(Enumerable.Repeat(Emoji, 10)), length)!;

            await Assert.That(cut.Length).IsLessThanOrEqualTo(length);
            await Assert.That((cut.Length == 0) || (char.IsHighSurrogate(cut[^1]) == false)).IsTrue().Because($"a cut to {length} characters must not end in half a pair");
        }
    }
}
