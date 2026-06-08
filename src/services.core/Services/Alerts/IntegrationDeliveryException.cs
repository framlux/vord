// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Thrown by <see cref="AlertDeliveryService"/> when one or more integration deliveries fail
/// transiently (HTTP 5xx or transport error). Propagates out of the Hangfire
/// <see cref="IntegrationDeliveryJob"/> so Hangfire's <c>[AutomaticRetry]</c> applies. Only
/// transient failures throw; permanent 4xx failures are logged without throwing because retry
/// would not help.
/// </summary>
public sealed class IntegrationDeliveryException : Exception
{
    /// <summary>
    /// Creates a new <see cref="IntegrationDeliveryException"/> with the specified message.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public IntegrationDeliveryException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="IntegrationDeliveryException"/> with the specified message and inner exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public IntegrationDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
