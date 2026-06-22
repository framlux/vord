// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>SshSessions-derived detail payload.</summary>
/// <param name="SshSessions">The raw SSH-sessions JSON payload (detail column).</param>
internal sealed record SshSessionsFragment(string SshSessions);
