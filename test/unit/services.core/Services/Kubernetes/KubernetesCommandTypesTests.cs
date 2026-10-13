// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Kubernetes;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesCommandTypes"/>: which spellings count as a cluster reporter command and
/// which as a cleanup command.
/// </summary>
public sealed class KubernetesCommandTypesTests
{
    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("k8s_reporter_unenrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task IsReporterCommand_EachOfTheThreeExactTypes_IsTrue(string commandType)
    {
        await Assert.That(KubernetesCommandTypes.IsReporterCommand(commandType)).IsTrue();
    }

    [Test]
    [Arguments("K8S_REPORTER_ENROL")]
    [Arguments("k8s_reporter_Enrol")]
    [Arguments("K8s_Reporter_Unenrol")]
    [Arguments("k8s_reporter_UNINSTALL")]
    [Arguments(" k8s_reporter_enrol")]
    [Arguments("k8s_reporter_enrol ")]
    [Arguments("k8s_reporter_enrol\0")]
    [Arguments("k8s_reporter_destroy")]
    [Arguments("k8s_reporter_")]
    [Arguments("reboot")]
    [Arguments("kill_process")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task IsReporterCommand_AnyOtherSpelling_IsFalse(string commandType)
    {
        // Intent: the match is ordinal and exact, the same way the agent matches. Every other spelling is
        // not a reporter command, so it faces the Team gate like any other command and is refused by the
        // service's own checks.
        await Assert.That(KubernetesCommandTypes.IsReporterCommand(commandType)).IsFalse();
    }

    [Test]
    [Arguments("k8s_reporter_unenrol")]
    [Arguments("k8s_reporter_uninstall")]
    public async Task IsCleanupCommand_UnenrolAndUninstall_IsTrue(string commandType)
    {
        await Assert.That(KubernetesCommandTypes.IsCleanupCommand(commandType)).IsTrue();
    }

    [Test]
    [Arguments("k8s_reporter_enrol")]
    [Arguments("K8S_REPORTER_UNENROL")]
    [Arguments("k8s_reporter_Uninstall")]
    [Arguments("k8s_reporter_uninstall ")]
    [Arguments("reboot")]
    [Arguments("")]
    public async Task IsCleanupCommand_EnrolAndEveryOtherSpelling_IsFalse(string commandType)
    {
        // Intent: enrol installs something, so it is not cleanup, and the exact-match rule means a changed
        // case can never earn the capability bypass.
        await Assert.That(KubernetesCommandTypes.IsCleanupCommand(commandType)).IsFalse();
    }

    [Test]
    public async Task EveryCleanupCommand_IsAReporterCommand()
    {
        string[] cleanup = [KubernetesCommandTypes.Unenrol, KubernetesCommandTypes.Uninstall];

        foreach (string commandType in cleanup)
        {
            await Assert.That(KubernetesCommandTypes.IsReporterCommand(commandType)).IsTrue();
        }
    }
}
