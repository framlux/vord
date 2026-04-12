// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Grpc.AgentRegistration;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for managing machine registrations and related operations.
/// </summary>
public interface IMachineService
{
    /// <summary>
    /// Gets the registration status of a machine based on its serial number, system ID, and registration token.
    /// Used for recovery when a RegisterSystem call succeeded on the server but the response was lost.
    /// When needsApiKey is true and no cached key exists, generates a new API key for the machine.
    /// </summary>
    /// <param name="serialNumber">The system serial number.</param>
    /// <param name="systemId">The motherboard ID.</param>
    /// <param name="registrationToken">The registration token used during initial registration.</param>
    /// <param name="needsApiKey">Whether the agent needs a new API key issued.</param>
    /// <param name="cancellationToken">Token used to cancel long running tasks.</param>
    /// <returns>Returns the registration status, machine ID, and API key.</returns>
    Task<(RegistrationStatus status, long? id, string? apiKey)> GetRegistrationStatusAsync(string serialNumber, string systemId, string registrationToken, bool needsApiKey, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a new system, creates a Machine record immediately, and returns the API key.
    /// </summary>
    /// <param name="request">The registration request</param>
    /// <param name="cancellationToken">Token used to cancel long running tasks</param>
    /// <returns>Returns a machine ID and API key, or error message</returns>
    Task<(long? machineId, string? apiKey, string errorMessage)> RegisterSystemAsync(RegisterSystemRequest request, CancellationToken cancellationToken);
}
