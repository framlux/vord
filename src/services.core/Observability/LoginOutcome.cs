// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// How one login attempt ended.
/// </summary>
public enum LoginOutcome
{
    /// <summary>The user signed in.</summary>
    Succeeded,

    /// <summary>The system correctly refused the user — an inactive or unauthorised account. A
    /// support question, not an incident.</summary>
    Rejected,

    /// <summary>The flow broke: a misconfigured provider, a token exchange that did not complete,
    /// or an identity token that would not validate. Nobody can sign in through that path.</summary>
    Failed,
}
