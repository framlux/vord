// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for how <see cref="KubernetesNodePayloadNormaliser"/> treats the reporter error a node report carries.
/// The agent sends one of two reason codes when its cluster reporter cannot use its credentials; the server
/// stores nothing else, so a code it does not know, or a report from an agent that sends none, reads back as no
/// error.
/// </summary>
public class KubernetesNodePayloadNormaliserTests
{
    private static KubernetesNodePayload Payload(string? reporterError)
    {
        return new KubernetesNodePayload
        {
            Distro = "k3s",
            NodeName = "cp-1",
            NodeNameSource = "certificate",
            HasKubelet = true,
            ControlPlane = true,
            ReporterEnrolled = true,
            ReporterError = reporterError,
        };
    }

    [Test]
    [Arguments("credentials_rejected")]
    [Arguments("tls_ca_changed")]
    public async Task Normalise_KnownReporterError_PassesThrough(string reporterError)
    {
        KubernetesNodePayload? normalised = KubernetesNodePayloadNormaliser.Normalise(Payload(reporterError));

        await Assert.That(normalised!.ReporterError).IsEqualTo(reporterError);
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    [Arguments("api_unreachable")]
    [Arguments("namespace_terminating")]
    [Arguments("Credentials_Rejected")]
    [Arguments("credentials_rejected ")]
    [Arguments(" tls_ca_changed")]
    [Arguments("anything else")]
    public async Task Normalise_UnknownOrEmptyReporterError_BecomesNull(string? reporterError)
    {
        KubernetesNodePayload? normalised = KubernetesNodePayloadNormaliser.Normalise(Payload(reporterError));

        await Assert.That(normalised).IsNotNull();
        await Assert.That(normalised!.ReporterError).IsNull();
    }

    [Test]
    public async Task Normalise_ReporterErrorLongerThanAnyCode_BecomesNull()
    {
        KubernetesNodePayload? normalised = KubernetesNodePayloadNormaliser.Normalise(Payload(new string('x', 10_000)));

        await Assert.That(normalised!.ReporterError).IsNull();
    }

    [Test]
    public async Task Normalise_ReportWithoutAnError_KeepsEveryOtherField()
    {
        KubernetesNodePayload? normalised = KubernetesNodePayloadNormaliser.Normalise(Payload(null));

        await Assert.That(normalised!.Distro).IsEqualTo("k3s");
        await Assert.That(normalised.NodeName).IsEqualTo("cp-1");
        await Assert.That(normalised.NodeNameSource).IsEqualTo("certificate");
        await Assert.That(normalised.HasKubelet).IsTrue();
        await Assert.That(normalised.ControlPlane).IsTrue();
        await Assert.That(normalised.ReporterEnrolled).IsTrue();
    }

    [Test]
    public async Task Deserialise_StoredSnakeCasePayload_ReadsReporterError()
    {
        // The agent's field is reporter_error; the stored payload is the serialised proto message, so the
        // payload class must read exactly that key.
        const string stored = "{\"distro\":\"k3s\",\"node_name\":\"cp-1\",\"has_kubelet\":true,\"reporter_enrolled\":true,\"reporter_error\":\"tls_ca_changed\"}";

        KubernetesNodePayload? payload = JsonSerializer.Deserialize<KubernetesNodePayload>(stored, JsonDefaults.SnakeCase);

        await Assert.That(payload!.ReporterError).IsEqualTo("tls_ca_changed");
    }

    [Test]
    public async Task Deserialise_PayloadFromAnAgentWithoutTheField_HasNoReporterError()
    {
        const string stored = "{\"distro\":\"k3s\",\"node_name\":\"cp-1\",\"has_kubelet\":true}";

        KubernetesNodePayload? payload = JsonSerializer.Deserialize<KubernetesNodePayload>(stored, JsonDefaults.SnakeCase);

        await Assert.That(payload!.ReporterError).IsNull();
    }

    // ========== NormaliseJson ==========

    [Test]
    public async Task NormaliseJson_StoredReport_ReadsAndNormalisesIt()
    {
        const string stored = "{\"distro\":\"docker-desktop\",\"kubelet_version\":\"v1.30.4+k3s1\",\"node_name\":\"cp-1\",\"has_kubelet\":true,\"control_plane\":true,\"admin_credential_present\":true,\"reporter_enrolled\":true}";

        KubernetesNodePayload? payload = KubernetesNodePayloadNormaliser.NormaliseJson(stored);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.Distro).IsEqualTo("unknown");
        await Assert.That(payload.NodeName).IsEqualTo("cp-1");
        await Assert.That(payload.ControlPlane).IsTrue();
        await Assert.That(payload.AdminCredentialPresent).IsTrue();
        await Assert.That(payload.ReporterEnrolled).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("{ \"distro\": ")]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("{\"distro\":\"k3s\",\"node_name\":\"Not_A_Node\",\"has_kubelet\":true}")]
    public async Task NormaliseJson_ReportThatCannotBeReadOrThatTheProjectionWouldRefuse_IsNull(string stored)
    {
        // Intent: every reader of a stored report must agree on what counts as one, so text that does not
        // parse and a report that normalisation refuses are both simply no report.
        await Assert.That(KubernetesNodePayloadNormaliser.NormaliseJson(stored)).IsNull();
    }
}
