// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// What an email was for. Closed at two values because the email interface offers exactly two
/// sends; a third purpose is a deliberate addition here, not an incidental one.
/// </summary>
public enum EmailPurpose
{
    /// <summary>A tenant invitation.</summary>
    Invitation,

    /// <summary>An alert notification.</summary>
    Alert,
}
