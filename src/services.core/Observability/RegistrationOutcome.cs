// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// How one machine registration attempt ended. Every member corresponds to a return from the
/// registration path, or to it throwing; the token failures are kept apart because they call for
/// different answers to the customer asking why onboarding did not work.
/// </summary>
public enum RegistrationOutcome
{
    /// <summary>A machine was registered and issued an API key.</summary>
    Registered,

    /// <summary>No registration token was supplied.</summary>
    MissingToken,

    /// <summary>The token supplied matches nothing.</summary>
    InvalidToken,

    /// <summary>The token was revoked by an administrator.</summary>
    RevokedToken,

    /// <summary>The token passed its expiry.</summary>
    ExpiredToken,

    /// <summary>The token was already used, including when a concurrent registration won the race.</summary>
    ConsumedToken,

    /// <summary>The machine is already registered.</summary>
    DuplicateMachine,

    /// <summary>The tenant is at its tier's machine limit. A paying customer who cannot onboard.</summary>
    MachineLimitExceeded,

    /// <summary>Registration threw. The machine row may already exist, so this is not simply a
    /// failed attempt — it is one that needs looking at.</summary>
    Error,
}
