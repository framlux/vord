// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="DataExportBackgroundService"/>.
/// </summary>
public sealed class DataExportBackgroundServiceTests
{
    private static (
        DataExportBackgroundService Service,
        IDistributedLock DistributedLock,
        IDataExportHandler Handler,
        ILogger<DataExportBackgroundService> Logger,
        TestDatabaseFactory DbFactory
    ) CreateSut()
    {
        TestDatabaseFactory dbFactory = new();
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        ILogger<DataExportBackgroundService> logger = Substitute.For<ILogger<DataExportBackgroundService>>();

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        DataExportBackgroundService service = new(scopeFactory, distributedLock, handler, logger);

        return (service, distributedLock, handler, logger, dbFactory);
    }

    private static async Task SeedPendingJob(DatabaseContext db, int tenantId = 1, int requestedByUserId = 1)
    {
        await db.InsertAsync(new DataExportJob
        {
            TenantId = tenantId,
            Status = DataExportJobStatus.Pending,
            RequestedByUserId = requestedByUserId,
            RequestedAt = DateTimeOffset.UtcNow,
            ObjectKey = "",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DownloadToken = Guid.NewGuid().ToString("N")
        });
    }

    [Test]
    public async Task ProcessPendingJobs_LockAcquired_CallsHandler()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> _, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();
            await SeedPendingJob(dbFactory.Context);

            LockHandle lockHandle = new(Substitute.For<IDatabase>(), "test-key", "test-value");
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(lockHandle);

            handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    workDone.TrySetResult();

                    return Task.CompletedTask;
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            await handler.Received(1).ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task ProcessPendingJobs_LockNotAcquired_HandlerNeverCalled()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> _, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();

            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(callInfo =>
                {
                    workDone.TrySetResult();

                    return (LockHandle?)null;
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task ProcessPendingJobs_MultipleJobs_ProcessesAll()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> _, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();
            int processedCount = 0;

            await SeedPendingJob(dbFactory.Context);
            await SeedPendingJob(dbFactory.Context);
            await SeedPendingJob(dbFactory.Context);

            LockHandle lockHandle = new(Substitute.For<IDatabase>(), "test-key", "test-value");
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(lockHandle);

            handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    if (Interlocked.Increment(ref processedCount) >= 3)
                    {
                        workDone.TrySetResult();
                    }

                    return Task.CompletedTask;
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            await handler.Received(3).ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task ProcessPendingJobs_HandlerThrows_ServiceContinues()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> logger, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();
            await SeedPendingJob(dbFactory.Context);

            LockHandle lockHandle = new(Substitute.For<IDatabase>(), "test-key", "test-value");
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(lockHandle);

            handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(callInfo =>
                {
                    workDone.TrySetResult();

                    return new InvalidOperationException("processing failed");
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            logger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Test]
    public async Task ProcessPendingJobs_LockAcquisitionThrows_ServiceContinues()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> logger, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();

            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .ThrowsAsync(callInfo =>
                {
                    workDone.TrySetResult();

                    return new InvalidOperationException("Redis down");
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
            logger.Received().Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Any<Exception>(),
                Arg.Any<Func<object, Exception?, string>>());
        }
    }

    [Test]
    public async Task ProcessPendingJobs_LockAcquiredWithCorrectKeyAndTtl()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler _, ILogger<DataExportBackgroundService> _, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            TaskCompletionSource workDone = new();

            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(callInfo =>
                {
                    workDone.TrySetResult();

                    return (LockHandle?)null;
                });

            using CancellationTokenSource cts = new();
            await service.StartAsync(cts.Token);
            await workDone.Task;
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);

            await distributedLock.Received().TryAcquireAsync("lock:data-export", TimeSpan.FromMinutes(30));
        }
    }

    [Test]
    public async Task ProcessPendingJobs_CancellationStopsJobProcessing()
    {
        (DataExportBackgroundService service, IDistributedLock distributedLock, IDataExportHandler handler, ILogger<DataExportBackgroundService> _, TestDatabaseFactory dbFactory) = CreateSut();
        using (dbFactory)
        {
            using CancellationTokenSource cts = new();

            await SeedPendingJob(dbFactory.Context);
            await SeedPendingJob(dbFactory.Context);

            LockHandle lockHandle = new(Substitute.For<IDatabase>(), "test-key", "test-value");
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(lockHandle);

            TaskCompletionSource workDone = new();

            handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    await cts.CancelAsync();
                    workDone.TrySetResult();
                });

            await service.StartAsync(cts.Token);
            await workDone.Task;
            await service.StopAsync(CancellationToken.None);

            // Should process at most 1 job before cancellation takes effect
            await handler.Received(1).ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
    }
}
