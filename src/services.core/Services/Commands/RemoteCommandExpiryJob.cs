// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Commands;

/// <summary>
/// Hangfire recurring job that expires pending remote commands that have passed their ExpiresAt time.
/// Replaces the former CommandExpiryBackgroundService.
/// </summary>
public sealed class RemoteCommandExpiryJob
{
    private readonly IRemoteCommandRepository _remoteCommandRepository;
    private readonly ILogger<RemoteCommandExpiryJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="RemoteCommandExpiryJob"/> class.
    /// </summary>
    /// <param name="remoteCommandRepository">The remote command repository.</param>
    /// <param name="logger">The logger.</param>
    public RemoteCommandExpiryJob(
        IRemoteCommandRepository remoteCommandRepository,
        ILogger<RemoteCommandExpiryJob> logger)
    {
        ArgumentNullException.ThrowIfNull(remoteCommandRepository);
        ArgumentNullException.ThrowIfNull(logger);

        _remoteCommandRepository = remoteCommandRepository;
        _logger = logger;
    }

    /// <summary>
    /// Runs the expiry pass.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 90)]
    [AutomaticRetry(Attempts = 0)]
    [Queue("critical")]
    public async Task RunAsync(CancellationToken ct)
    {
        await _remoteCommandRepository.ExpirePendingCommandsAsync(ct);
    }
}
