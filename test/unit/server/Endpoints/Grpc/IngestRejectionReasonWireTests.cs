// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Grpc.AgentCommon;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Grpc.AgentTelemetry;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// Pins the numbers the reason code travels as. The Go agent generates its own bindings from the
/// same proto, so a renumbered enum member or field is read as a different state by agents already
/// in the field, and nothing in either build would notice.
/// </summary>
public sealed class IngestRejectionReasonWireTests
{
    [Test]
    public async Task IngestRejectionReason_HasExactlyTheAgreedWireValues()
    {
        Dictionary<IngestRejectionReason, int> wire = Enum.GetValues<IngestRejectionReason>()
            .ToDictionary(reason => reason, reason => (int)reason);

        await Assert.That(wire.Count).IsEqualTo(3);
        await Assert.That(wire[IngestRejectionReason.Unspecified]).IsEqualTo(0);
        await Assert.That(wire[IngestRejectionReason.HostPaused]).IsEqualTo(1);
        await Assert.That(wire[IngestRejectionReason.SubscriptionInactive]).IsEqualTo(2);
    }

    [Test]
    public async Task TelemetryAck_CarriesTheReasonOnFieldFive()
    {
        await Assert.That(TelemetryAck.Descriptor.FindFieldByName("reason").FieldNumber).IsEqualTo(5);
    }

    [Test]
    public async Task AgentPingResponse_CarriesTheStateOnFieldTwo()
    {
        await Assert.That(AgentPingResponse.Descriptor.FindFieldByName("state").FieldNumber).IsEqualTo(2);
    }

    [Test]
    public async Task IngestRejectionReason_DefaultsToUnspecifiedSoOldServersReadAsNotRejected()
    {
        // proto3 omits a zero value on the wire, which is how an ack from a server that predates the
        // field arrives; it must read as "no reason" and not as a rejection.
        await Assert.That(new TelemetryAck().Reason).IsEqualTo(IngestRejectionReason.Unspecified);
        await Assert.That(new AgentPingResponse().State).IsEqualTo(IngestRejectionReason.Unspecified);
    }
}
