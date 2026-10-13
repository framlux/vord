// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Test.Infrastructure;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="CanonicalPayloadBinding"/>, which compares the command type and parameters a
/// signed canonical payload carries with the ones the request carries, by the agent's rules.
/// </summary>
public sealed class CanonicalPayloadBindingTests
{
    [Test]
    [Arguments("reboot", null)]
    [Arguments("reboot", "")]
    [Arguments("reboot", "  ")]
    [Arguments("reboot", "{}")]
    public async Task Matches_NoParamsOnTheRequestAndAnEmptyObjectSigned_IsTrue(string commandType, string? requestParams)
    {
        string payload = TestCanonicalPayload.Build(commandType, "{}");

        await Assert.That(CanonicalPayloadBinding.Matches(payload, commandType, requestParams)).IsTrue();
    }

    [Test]
    public async Task Matches_NoParamsKeyInThePayload_IsTheSameAsAnEmptyObject()
    {
        string payload = TestCanonicalPayload.Build("reboot").Replace("\"params\":{},", string.Empty);

        await Assert.That(payload).DoesNotContain("params");
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "reboot", null)).IsTrue();
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "reboot", "{}")).IsTrue();
    }

    [Test]
    public async Task Matches_EqualParamsInAnyOrderAndFormatting_IsTrue()
    {
        string payload = TestCanonicalPayload.Build("kill_process", """{"pid":"42","signal":"9"}""");

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", """{ "signal" : "9", "pid" : "42" }""")).IsTrue();
    }

    [Test]
    public async Task Matches_ParamValueWithEscapes_IsComparedByItsDecodedText()
    {
        string payload = TestCanonicalPayload.Build("kill_process", """{"name":"café"}""");

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", "{\"name\":\"café\"}")).IsTrue();
    }

    [Test]
    [Arguments("reboot", "Reboot")]
    [Arguments("reboot", "REBOOT")]
    [Arguments("reboot", "reboot ")]
    [Arguments("k8s_reporter_enrol", "K8S_REPORTER_ENROL")]
    [Arguments("reboot", "check_updates")]
    [Arguments("reboot", "")]
    public async Task Matches_CommandTypeThatDiffersFromTheRequestsInAnyWay_IsFalse(string signedType, string requestType)
    {
        // Intent: the comparison is ordinal. The Team gate and the agent both treat the type as an exact
        // string, so a case-insensitive match here would be a way around the gate.
        string payload = TestCanonicalPayload.Build(signedType);

        await Assert.That(CanonicalPayloadBinding.Matches(payload, requestType, null)).IsFalse();
    }

    [Test]
    [Arguments("""{"pid":"42"}""", null)]
    [Arguments("""{"pid":"42"}""", "{}")]
    [Arguments("{}", """{"pid":"42"}""")]
    [Arguments("""{"pid":"42"}""", """{"pid":"43"}""")]
    [Arguments("""{"pid":"42"}""", """{"pid":"42","signal":"9"}""")]
    [Arguments("""{"pid":"42","signal":"9"}""", """{"pid":"42"}""")]
    [Arguments("""{"pid":"42"}""", """{"PID":"42"}""")]
    [Arguments("""{"pid":"42"}""", """{"pid":"42 "}""")]
    [Arguments("""{"pid":"42"}""", """{"pid42":"42"}""")]
    public async Task Matches_ParamsThatDifferFromTheRequestsInAnyWay_IsFalse(string signedParams, string? requestParams)
    {
        string payload = TestCanonicalPayload.Build("kill_process", signedParams);

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", requestParams)).IsFalse();
    }

    [Test]
    [Arguments("""{"pid":42}""")]
    [Arguments("""{"pid":null}""")]
    [Arguments("""{"pid":["42"]}""")]
    [Arguments("""{"pid":"1","pid":"1"}""")]
    [Arguments("[]")]
    [Arguments("null")]
    [Arguments("\"x\"")]
    [Arguments("1")]
    public async Task Matches_SignedParamsThatAreNotAnObjectOfUniqueStrings_IsFalse(string signedParams)
    {
        // Intent: the agent refuses such a payload outright, so the server never calls it a match.
        string payload = TestCanonicalPayload.Build("kill_process", signedParams);

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", null)).IsFalse();
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", "{}")).IsFalse();
    }

    [Test]
    [Arguments("""{"pid":42}""")]
    [Arguments("[]")]
    [Arguments("not json")]
    [Arguments("""{"pid":"1","pid":"2"}""")]
    public async Task Matches_RequestParamsThatAreNotAnObjectOfUniqueStrings_IsFalse(string requestParams)
    {
        string payload = TestCanonicalPayload.Build("kill_process", "{}");

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", requestParams)).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("null")]
    [Arguments("\"command_type\"")]
    [Arguments("{")]
    [Arguments("{}")]
    [Arguments("""{"params":{}}""")]
    [Arguments("""{"command_type":null,"params":{}}""")]
    [Arguments("""{"command_type":5,"params":{}}""")]
    [Arguments("""{"command_type":["reboot"],"params":{}}""")]
    [Arguments("""{"command_type":"reboot","params":{}} {"command_type":"reboot"}""")]
    [Arguments("""{"command_type":"reboot","params":{}} trailing""")]
    public async Task Matches_PayloadThatIsNotAnObjectWithAStringCommandType_IsFalse(string payload)
    {
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "reboot", null)).IsFalse();
    }

    [Test]
    [Arguments("""{"command_type":"reboot","command_type":"reboot","params":{}}""")]
    [Arguments("""{"command_type":"reboot","params":{},"params":{}}""")]
    [Arguments("""{"command_type":"reboot","params":{},"nonce":"a","nonce":"b"}""")]
    [Arguments("""{"command_type":"reboot","Command_Type":"reboot","params":{}}""")]
    [Arguments("""{"command_type":"reboot","PARAMS":{}}""")]
    [Arguments("""{"command_type":"reboot","Params":{}}""")]
    [Arguments("""{"command_type":"reboot","command_type":"reboot","params":{}}""")]
    public async Task Matches_DuplicateOrCaseVariantKeys_IsFalse(string payload)
    {
        // Intent: a reader that keeps the first or last duplicate, or matches keys without regard to case,
        // can be made to see a different command than the agent, so the agent refuses such payloads and the
        // server does not call them a match.
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "reboot", null)).IsFalse();
    }

    [Test]
    [Arguments("""{"\ud800":"x"}""")]
    [Arguments("""{"a":"\ud800"}""")]
    public async Task Matches_LoneSurrogateEscapeInSignedOrRequestedParams_IsFalseWithoutThrowing(string paramsJson)
    {
        string payload = TestCanonicalPayload.Build("kill_process", paramsJson);

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", null)).IsFalse();
        await Assert.That(CanonicalPayloadBinding.Matches(TestCanonicalPayload.Build("kill_process"), "kill_process", paramsJson)).IsFalse();
    }

    [Test]
    [Arguments("""{"a":"\u0000"}""")]
    [Arguments("""{"\u0000":"x"}""")]
    public async Task Matches_NulCharacterInSignedOrRequestedParams_IsFalse(string paramsJson)
    {
        // Intent: the same params shape judges the signed bytes and the request, and neither may hold a
        // NUL, which the stored jsonb column cannot represent. Both sides agreeing on one is not a match.
        string payload = TestCanonicalPayload.Build("kill_process", paramsJson);

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", paramsJson)).IsFalse();
        await Assert.That(CanonicalPayloadBinding.Matches(payload, "kill_process", null)).IsFalse();
        await Assert.That(CanonicalPayloadBinding.Matches(TestCanonicalPayload.Build("kill_process"), "kill_process", paramsJson)).IsFalse();
    }

    [Test]
    public async Task Matches_OtherKeysAreLeftToTheAgent()
    {
        // Intent: the server compares only the two fields that decide whether a command may be sent. The id,
        // tenant, machine, user, nonce and timestamps are bound by the agent against the delivered command.
        string payload = """{"command_type":"reboot","params":{},"anything":[1,{"x":null}]}""";

        await Assert.That(CanonicalPayloadBinding.Matches(payload, "reboot", null)).IsTrue();
    }

    [Test]
    public async Task Matches_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.FromResult(CanonicalPayloadBinding.Matches(null!, "reboot", null)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Task.FromResult(CanonicalPayloadBinding.Matches("{}", null!, null)));
    }
}
