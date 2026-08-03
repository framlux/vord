// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Grpc.Core;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorises a caller of the internal gRPC services (billing gateway and fleet admin).
/// </summary>
public interface IInternalCallerAuthorizer
{
    /// <summary>
    /// Throws an <see cref="RpcException"/> unless the call carries an accepted internal
    /// identity. Implementations must fail closed: an unconfigured or unrecognised caller is
    /// rejected, never allowed through.
    /// </summary>
    /// <param name="context">The gRPC server call context.</param>
    void Authorize(ServerCallContext context);
}
