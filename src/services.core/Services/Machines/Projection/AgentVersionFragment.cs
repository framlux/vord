// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>AgentVersion-derived detail values.</summary>
/// <param name="AgentVersion">The running agent's build version (detail column).</param>
internal sealed record AgentVersionFragment(string? AgentVersion);
