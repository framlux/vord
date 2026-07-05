// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>Tests the out-of-band SSH alert evaluation job.</summary>
public class SshAlertEvaluationJobTests
{
    [Test]
    public async Task RunAsync_ConnectEvent_EvaluatesConnect()
    {
        IEventAlertService svc = Substitute.For<IEventAlertService>();
        SshAlertEvaluationJob job = new(svc, NullLogger<SshAlertEvaluationJob>.Instance);

        await job.RunAsync(
            tenantId: 7, machineId: 42, action: "connect",
            user: "root", sourceIp: "1.2.3.4", sourcePort: 22, authMethod: "publickey",
            CancellationToken.None);

        await svc.Received(1).EvaluateSshConnectAsync(7, 42, "root", "1.2.3.4", 22, "publickey", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_DisconnectEvent_ResolvesDisconnect()
    {
        IEventAlertService svc = Substitute.For<IEventAlertService>();
        SshAlertEvaluationJob job = new(svc, NullLogger<SshAlertEvaluationJob>.Instance);

        await job.RunAsync(7, 42, "disconnect", "root", "1.2.3.4", 22, "publickey", CancellationToken.None);

        await svc.Received(1).ResolveSshDisconnectAsync(42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_UnknownAction_DoesNothing()
    {
        IEventAlertService svc = Substitute.For<IEventAlertService>();
        SshAlertEvaluationJob job = new(svc, NullLogger<SshAlertEvaluationJob>.Instance);

        await job.RunAsync(7, 42, "rekey", "root", "1.2.3.4", 22, "publickey", CancellationToken.None);

        await svc.DidNotReceive().EvaluateSshConnectAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await svc.DidNotReceive().ResolveSshDisconnectAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_FailedAction_DoesNothing()
    {
        // A "failed" item that somehow reaches the job (defense in depth) is still a no-op.
        IEventAlertService svc = Substitute.For<IEventAlertService>();
        SshAlertEvaluationJob job = new(svc, NullLogger<SshAlertEvaluationJob>.Instance);

        await job.RunAsync(7, 42, "failed", "root", "1.2.3.4", 22, "password", CancellationToken.None);

        await svc.DidNotReceive().EvaluateSshConnectAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await svc.DidNotReceive().ResolveSshDisconnectAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsEvaluatedAction_OnlyConnectAndDisconnect_CaseInsensitive()
    {
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction("connect")).IsTrue();
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction("disconnect")).IsTrue();
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction("CONNECT")).IsTrue();
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction("failed")).IsFalse();
        await Assert.That(SshAlertEvaluationJob.IsEvaluatedAction("rekey")).IsFalse();
    }
}
