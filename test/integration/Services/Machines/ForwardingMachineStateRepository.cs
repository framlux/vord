// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using System.Reflection;

namespace Framlux.FleetManagement.Test.Integration.Services.Machines;

/// <summary>
/// Wraps a repository so a test can observe the projection loop: each call is forwarded, the
/// telemetry poll is announced, and a failed call, synchronous or awaited, is reported.
/// </summary>
public class ForwardingMachineStateRepository : DispatchProxy
{
    private IMachineStateRepository _inner = default!;
    private Action _onPoll = default!;
    private Action<Exception> _onFailure = default!;

    /// <summary>Creates the wrapper around <paramref name="inner"/>.</summary>
    public static IMachineStateRepository Create(IMachineStateRepository inner, Action onPoll, Action<Exception> onFailure)
    {
        IMachineStateRepository proxy = DispatchProxy.Create<IMachineStateRepository, ForwardingMachineStateRepository>();
        ForwardingMachineStateRepository self = (ForwardingMachineStateRepository)(object)proxy;
        self._inner = inner;
        self._onPoll = onPoll;
        self._onFailure = onFailure;

        return proxy;
    }

    /// <inheritdoc/>
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            throw new InvalidOperationException("The call has no target method.");
        }

        if (targetMethod.Name == nameof(IMachineStateRepository.GetTelemetryBatchAsync))
        {
            _onPoll();
        }

        object? result;
        try
        {
            result = targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            _onFailure(ex.InnerException);

            throw ex.InnerException;
        }

        if (result is Task task)
        {
            _ = task.ContinueWith(
                completed => _onFailure(completed.Exception!.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return result;
    }
}
