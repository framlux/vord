// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Grpc.Core.Testing;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Stands up a functional host with a live Hangfire processing server, registers the recurring
/// jobs, and exposes a <see cref="FleetAdminService"/> ready to call directly.
/// </summary>
/// <remarks>
/// The service is built with <see cref="ActivatorUtilities"/> rather than resolved from the
/// container: gRPC services are created by the framework's service activator and are never
/// registered in DI, so <c>GetService&lt;FleetAdminService&gt;()</c> returns null. Caller
/// authorisation is substituted, matching the server unit tests — the authorizer's rules are
/// proven in <c>CertificateSubjectAuthorizerTests</c> and end to end in the functional gRPC
/// suite, so re-proving them here would add nothing.
/// </remarks>
public sealed class FleetAdminJobsFixture : IAsyncDisposable
{
    private FleetAdminJobsFixture(FunctionalTestFactory factory, FleetAdminService service)
    {
        Factory = factory;
        Service = service;
        CallContext = CreateContext();
    }

    /// <summary>The test host.</summary>
    public FunctionalTestFactory Factory { get; }

    /// <summary>The service under test.</summary>
    public FleetAdminService Service { get; }

    /// <summary>A call context with no peer identity, sufficient for a substituted authorizer.</summary>
    public ServerCallContext CallContext { get; }

    /// <summary>
    /// Creates the fixture.
    /// </summary>
    /// <param name="registerRecurringJobs">
    /// Whether to register the recurring jobs. Pass false to exercise the path where a job id is
    /// known to the build but absent from storage.
    /// </param>
    /// <returns>The fixture.</returns>
    public static Task<FleetAdminJobsFixture> CreateAsync(bool registerRecurringJobs = true)
    {
        FunctionalTestFactory factory = new();
        factory.EnableHangfireProcessingServer = true;

        if (registerRecurringJobs)
        {
            IRecurringJobManager manager = factory.Services.GetRequiredService<IRecurringJobManager>();
            RecurringJobRegistry.RegisterAll(manager, isSaas: true, objectStorageEnabled: true);
        }
        else
        {
            // Touching Services forces the host to build so the processing server starts.
            _ = factory.Services;
        }

        IInternalCallerAuthorizer authorizer = Substitute.For<IInternalCallerAuthorizer>();
        FleetAdminService service = ActivatorUtilities.CreateInstance<FleetAdminService>(
            factory.Services, authorizer);

        return Task.FromResult(new FleetAdminJobsFixture(factory, service));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Factory.Dispose();

        return ValueTask.CompletedTask;
    }

    private static ServerCallContext CreateContext()
    {
        Metadata headers = new();

        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
    }
}
