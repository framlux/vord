// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// The remote command types that drive the cluster reporter on a control-plane host. None of them takes
/// parameters, and each is accepted only for a machine that is eligible for it.
/// </summary>
public static class KubernetesCommandTypes
{
    /// <summary>Installs the reporter's Kubernetes objects and starts the host as a reporter candidate.</summary>
    public const string Enrol = "k8s_reporter_enrol";

    /// <summary>Removes this host's reporter state without touching the cluster-wide objects.</summary>
    public const string Unenrol = "k8s_reporter_unenrol";

    /// <summary>Removes every Kubernetes object the reporter installed, and this host's reporter state.</summary>
    public const string Uninstall = "k8s_reporter_uninstall";

    /// <summary>
    /// Reports whether a command type is one of the three cluster reporter commands. A constant pattern on a
    /// string compares ordinally, which is how the agent matches, so another spelling of one of them is not
    /// a reporter command.
    /// </summary>
    /// <param name="type">The command type exactly as submitted.</param>
    public static bool IsReporterCommand(string type)
    {
        return type is Enrol or Unenrol or Uninstall;
    }

    /// <summary>
    /// Reports whether a command type removes the reporter: unenrol or uninstall, matched ordinally and
    /// exactly. These can be sent to a host whose remote commands are switched off, because they only undo
    /// what an earlier enrolment installed.
    /// </summary>
    /// <param name="type">The command type exactly as submitted.</param>
    public static bool IsCleanupCommand(string type)
    {
        return type is Unenrol or Uninstall;
    }
}
