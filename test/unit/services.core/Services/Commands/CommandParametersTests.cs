// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Commands;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="CommandParameters"/>: the one definition of a command's parameters, a JSON object
/// whose every value is a string and whose keys are unique.
/// </summary>
public sealed class CommandParametersTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t\r\n")]
    [Arguments("{}")]
    [Arguments(" { } ")]
    public async Task TryParse_NullBlankOrAnEmptyObject_IsAnEmptyMap(string? json)
    {
        bool parsed = CommandParameters.TryParse(json, out Dictionary<string, string> parameters);

        await Assert.That(parsed).IsTrue();
        await Assert.That(parameters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryParse_ObjectOfStrings_ReturnsEveryPairExactly()
    {
        bool parsed = CommandParameters.TryParse("""{"pid":"42","signal":"","note":"a \"quoted\" word","unicode":"é"}""", out Dictionary<string, string> parameters);

        await Assert.That(parsed).IsTrue();
        await Assert.That(parameters.Count).IsEqualTo(4);
        await Assert.That(parameters["pid"]).IsEqualTo("42");
        await Assert.That(parameters["signal"]).IsEqualTo(string.Empty);
        await Assert.That(parameters["note"]).IsEqualTo("a \"quoted\" word");
        await Assert.That(parameters["unicode"]).IsEqualTo("é");
    }

    [Test]
    public async Task TryParse_KeysDifferingOnlyInCase_AreDistinct()
    {
        // Intent: the agent keys its map ordinally, so "PID" and "pid" are two parameters.
        bool parsed = CommandParameters.TryParse("""{"pid":"1","PID":"2"}""", out Dictionary<string, string> parameters);

        await Assert.That(parsed).IsTrue();
        await Assert.That(parameters["pid"]).IsEqualTo("1");
        await Assert.That(parameters["PID"]).IsEqualTo("2");
    }

    [Test]
    [Arguments("""{"pid":42}""")]
    [Arguments("""{"pid":4.2}""")]
    [Arguments("""{"flag":true}""")]
    [Arguments("""{"flag":false}""")]
    [Arguments("""{"pid":null}""")]
    [Arguments("""{"nested":{"a":"b"}}""")]
    [Arguments("""{"list":["a"]}""")]
    [Arguments("""{"ok":"1","pid":42}""")]
    [Arguments("[]")]
    [Arguments("""["a"]""")]
    [Arguments("42")]
    [Arguments("\"text\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("{")]
    [Arguments("""{"a":"b"}{"c":"d"}""")]
    [Arguments("""{"a":"b"} trailing""")]
    [Arguments("not json")]
    [Arguments("""{"pid":"1","pid":"2"}""")]
    [Arguments("""{"pid":"1","pid":"1"}""")]
    [Arguments("""{"pid":"1","pid":"2"}""")]
    public async Task TryParse_AnythingButAnObjectOfStringsWithUniqueKeys_IsRefused(string json)
    {
        bool parsed = CommandParameters.TryParse(json, out Dictionary<string, string> parameters);

        await Assert.That(parsed).IsFalse();
        await Assert.That(parameters.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"\ud800":"x"}""")]
    [Arguments("""{"\udc00":"x"}""")]
    [Arguments("""{"ok":"1","\ud800":"x"}""")]
    [Arguments("""{"a":"\ud800"}""")]
    [Arguments("""{"a":"x\udc00y"}""")]
    public async Task TryParse_LoneSurrogateEscapeInAKeyOrAValue_IsRefusedWithoutThrowing(string json)
    {
        // Intent: such text parses as JSON but cannot be decoded into a string, so it must read as "not an
        // object of strings" instead of escaping as an exception that the caller would turn into a 500.
        bool parsed = CommandParameters.TryParse(json, out Dictionary<string, string> parameters);

        await Assert.That(parsed).IsFalse();
        await Assert.That(parameters.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryParse_NestingBeyondTheParsersDepth_IsRefusedWithoutThrowing()
    {
        string json = new string('[', 200) + new string(']', 200);

        bool parsed = CommandParameters.TryParse(json, out _);

        await Assert.That(parsed).IsFalse();
    }
}
