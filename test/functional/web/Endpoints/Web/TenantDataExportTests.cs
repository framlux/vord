// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LinqToDB;
using LinqToDB.Async;
using NSubstitute;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the data export endpoints.
/// </summary>
public sealed class TenantDataExportTests
{
    // ========== Authorization tests ==========

    [Test]
    public async Task RequestExport_Unauthenticated_Returns401()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        // The authentication middleware short-circuits before the handler runs and emits an empty
        // body for a 401. Assert the response is empty so that any future regression which leaks
        // a payload through the unauthenticated path will fail this test.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task RequestExport_ViewerRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // The TenantAdmin authorization policy denies Viewer role before the handler runs, so
        // ASP.NET emits an empty body. Asserting empty catches a future regression where any
        // handler-produced payload (which would necessarily mean the policy was bypassed) leaks.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).IsEqualTo(string.Empty);

        // No export job row should exist because we never reached the handler.
        bool anyJob = await db.DataExportJobs.AnyAsync(j => j.TenantId == tenantId);
        await Assert.That(anyJob).IsFalse();
    }

    [Test]
    public async Task RequestExport_MachineAdminRole_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.MachineAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

        // The TenantAdmin authorization policy denies MachineAdmin role before the handler runs,
        // so ASP.NET emits an empty body. Empty-body assertion catches a regression where the
        // policy is loosened and the handler returns a success payload.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).IsEqualTo(string.Empty);

        // No export job row should exist because we never reached the handler.
        bool anyJob = await db.DataExportJobs.AnyAsync(j => j.TenantId == tenantId);
        await Assert.That(anyJob).IsFalse();
    }

    [Test]
    public async Task RequestExport_TenantAdminRole_Returns200WithJobId()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");
        await SeedMachine(db, tenantId, "export-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The response is the standard ApiResponse envelope; the web client unwraps
        // "data" to obtain the job, so the shape here is load-bearing.
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsTrue();

        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("jobId").GetInt32()).IsGreaterThan(0);
        await Assert.That(data.GetProperty("status").GetString()).IsEqualTo("Pending");
    }

    [Test]
    public async Task RequestExport_TenantAdminRole_PersistsJobAndEnqueuesHangfireRun()
    {
        // Intent: a successful POST must (1) create the DataExportJob row so the recurring
        // safety-net job can also pick it up, and (2) immediately enqueue DataExportProcessingJob
        // via IBackgroundJobClient so the user does not wait for the next cron tick.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Enqueue Tenant");
        await SeedMachine(db, tenantId, "enqueue-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        int jobId = doc.RootElement.GetProperty("data").GetProperty("jobId").GetInt32();

        // Database row was created and is associated with the requesting tenant.
        DataExportJob? row = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == jobId);
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.TenantId).IsEqualTo(tenantId);

        // Hangfire enqueue was invoked exactly once with the per-job claim path. The endpoint now
        // routes to ProcessSingleAsync(jobId) (rather than fleet-wide RunAsync) so we get sub-
        // minute pickup of EXACTLY this row. The jobId argument must match the row id returned
        // in the response body — a transposition bug would route the claim against the wrong row.
        factory.BackgroundJobClientMock.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(DataExportProcessingJob)
                          && j.Method.Name == nameof(DataExportProcessingJob.ProcessSingleAsync)
                          && (int)j.Args[0]! == jobId),
            Arg.Any<EnqueuedState>());
    }

    // ========== Status endpoint tests ==========

    [Test]
    public async Task ExportStatus_WrongTenant_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenant1Id = await SeedTenantWithSubscription(db, "Tenant 1");
        int tenant2Id = await SeedTenantWithSubscription(db, "Tenant 2");
        await SeedMachine(db, tenant1Id, "t1-host");

        // Create export as tenant 1
        HttpClient client1 = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenant1Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant1Id)
            .Build();

        HttpResponseMessage createResponse = await client1.PostAsync("/api/v1/tenants/export", null);
        string createBody = await createResponse.Content.ReadAsStringAsync();

        // Extract jobId from response using proper JSON deserialization.
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        int jobId = createDoc.RootElement.GetProperty("data").GetProperty("jobId").GetInt32();
        string jobIdStr = jobId.ToString();

        // Try to access from tenant 2
        HttpClient client2 = new AuthenticatedClientBuilder(factory)
            .WithUserId(2)
            .WithRole(tenant2Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant2Id)
            .Build();

        HttpResponseMessage statusResponse = await client2.GetAsync($"/api/v1/tenants/export/{jobIdStr}");

        await Assert.That(statusResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // Tenant 2 must see the same 404 message regardless of whether the job exists for another
        // tenant — otherwise we'd leak existence to cross-tenant probes.
        string statusBody = await statusResponse.Content.ReadAsStringAsync();
        await Assert.That(statusBody).Contains("Export job not found");
    }

    [Test]
    public async Task ExportStatus_ValidJob_ReturnsCorrectStatus()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Test Tenant");
        await SeedMachine(db, tenantId, "status-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage createResponse = await client.PostAsync("/api/v1/tenants/export", null);
        string createBody = await createResponse.Content.ReadAsStringAsync();

        // Extract jobId from response using proper JSON deserialization.
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        int jobId = createDoc.RootElement.GetProperty("data").GetProperty("jobId").GetInt32();
        string jobIdStr = jobId.ToString();

        HttpResponseMessage statusResponse = await client.GetAsync($"/api/v1/tenants/export/{jobIdStr}");

        await Assert.That(statusResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string statusBody = await statusResponse.Content.ReadAsStringAsync();
        await Assert.That(statusBody).Contains("\"status\":\"Pending\"");
        await Assert.That(statusBody).Contains("\"jobId\":");
    }

    // ========== Empty state tests ==========

    [Test]
    public async Task RequestExport_TenantWithNoMachines_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Empty Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The endpoint surfaces ServiceResult.NotFound() (also raised when the tenant has no
        // machines to export) as the "Tenant not found" envelope error.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Tenant not found");

        // No export job was created on a NotFound path.
        bool anyJob = await db.DataExportJobs.AnyAsync(j => j.TenantId == tenantId);
        await Assert.That(anyJob).IsFalse();
    }

    // ========== Error envelope tests ==========

    [Test]
    public async Task RequestExport_ObjectStorageNotConfigured_Returns501ErrorEnvelope()
    {
        using ObjectStorageDisabledTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "No Storage Tenant");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That((int)response.StatusCode).IsEqualTo(501);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString())
            .IsEqualTo("Data export is not available on this server");
    }

    [Test]
    public async Task RequestExport_AlreadyInProgress_Returns409ErrorEnvelope()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Duplicate Export Tenant");
        await SeedMachine(db, tenantId, "duplicate-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage firstResponse = await client.PostAsync("/api/v1/tenants/export", null);
        await Assert.That(firstResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        HttpResponseMessage secondResponse = await client.PostAsync("/api/v1/tenants/export", null);

        await Assert.That((int)secondResponse.StatusCode).IsEqualTo(409);

        string body = await secondResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString())
            .IsEqualTo("A data export is already in progress");
    }

    [Test]
    public async Task ExportDownload_JobNotReady_Returns409ErrorEnvelope()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Pending Download Tenant");
        await SeedMachine(db, tenantId, "pending-host");

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage createResponse = await client.PostAsync("/api/v1/tenants/export", null);
        string createBody = await createResponse.Content.ReadAsStringAsync();
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        int jobId = createDoc.RootElement.GetProperty("data").GetProperty("jobId").GetInt32();

        HttpResponseMessage downloadResponse = await client.GetAsync($"/api/v1/tenants/export/{jobId}/download");

        await Assert.That((int)downloadResponse.StatusCode).IsEqualTo(409);

        string body = await downloadResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();

        string? message = doc.RootElement.GetProperty("message").GetString();
        await Assert.That(message).IsNotNull();
        await Assert.That(message!).Contains("Export is not ready for download");
    }

    [Test]
    public async Task ExportStatus_CompleteJob_ReturnsRoutePrefixedDownloadUrls()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenantWithSubscription(db, "Complete Job Tenant");

        DataExportJob job = new()
        {
            TenantId = tenantId,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            ObjectKey = "exports/complete-job-tenant.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            DownloadToken = Guid.NewGuid().ToString("N"),
            FileSizeBytes = 1024,
        };
        job.Id = await db.InsertWithInt32IdentityAsync(job);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();

        HttpResponseMessage response = await client.GetAsync($"/api/v1/tenants/export/{job.Id}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The download URLs must carry the real route prefix (api/v1) or the links the UI
        // renders will 404.
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("downloadUrl").GetString())
            .IsEqualTo($"/api/v1/tenants/export/{job.Id}/download");
        await Assert.That(data.GetProperty("shareableUrl").GetString())
            .IsEqualTo($"/api/v1/exports/download?token={job.DownloadToken}");
    }

    // ========== Seed helpers ==========

    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, string name)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        return tenant.Id;
    }

    private static async Task<long> SeedMachine(DatabaseContext db, int tenantId, string hostname)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        return machine.Id;
    }
}
