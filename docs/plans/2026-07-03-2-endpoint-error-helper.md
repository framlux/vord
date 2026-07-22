# Endpoint Error Helper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the 185 hand-written `StatusCode = n; WriteAsJsonAsync(ApiResponse<T>.Error(...))` blocks across 68 endpoint files with one shared `SendApiErrorAsync` extension.

**Architecture:** A single extension method on `HttpContext` in the server project writes the status code and a non-generic `ApiResponse<object>` error body. Error responses never populate `Data`, so the generic parameter in the existing per-endpoint calls has no effect on the wire format — `ApiResponse<object>.Error(msg)` serializes byte-identically to `ApiResponse<CancelSubscriptionResponse>.Error(msg)`. Replacement is therefore mechanical and wire-compatible.

**Tech Stack:** .NET 10, FastEndpoints, TUnit.

## Global Constraints

See [README.md](README.md#global-constraints). Depends on plan 1 (Task 1) having reduced `ApiResponse<T>` to the single two-parameter `Error` factory.

---

### Task 1: The `SendApiErrorAsync` extension (TDD)

**Files:**
- Create: `src/server/Endpoints/EndpointErrorExtensions.cs`
- Test: `test/unit/server/Endpoints/EndpointErrorExtensionsTests.cs`

**Interfaces:**
- Consumes: `ApiResponse<T>` from `Framlux.FleetManagement.Services.Core.Models`.
- Produces: `Task SendApiErrorAsync(this HttpContext httpContext, int statusCode, string message, CancellationToken ct)` — Tasks 2–4 and plan 3 call exactly this signature.

- [ ] **Step 1: Write the failing test**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints;
using Framlux.FleetManagement.Services.Core.Models;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Server.Endpoints;

/// <summary>
/// Tests for <see cref="EndpointErrorExtensions"/>.
/// </summary>
public class EndpointErrorExtensionsTests
{
    [Test]
    [Arguments(400, "Bad input")]
    [Arguments(401, "Unauthorized")]
    [Arguments(404, "Not found")]
    [Arguments(502, "Upstream failure")]
    public async Task SendApiErrorAsync_WritesStatusCodeAndErrorEnvelope(int statusCode, string message)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        await httpContext.SendApiErrorAsync(statusCode, message, CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        ApiResponse<object>? body = await JsonSerializer.DeserializeAsync<ApiResponse<object>>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(statusCode);
        await Assert.That(body).IsNotNull();
        await Assert.That(body?.Success).IsFalse();
        await Assert.That(body?.Message).IsEqualTo(message);
        await Assert.That(body?.Data).IsNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*EndpointErrorExtensions*"`
Expected: compile failure — `EndpointErrorExtensions` not defined.

- [ ] **Step 3: Write the implementation**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models;

namespace Framlux.FleetManagement.Server.Endpoints;

/// <summary>
/// Shared helper for writing <see cref="ApiResponse{T}"/> error envelopes from endpoint
/// handlers and pre-processors. Centralizes the status-code + JSON body idiom that was
/// previously hand-written per endpoint, so serializer behavior stays consistent.
/// </summary>
public static class EndpointErrorExtensions
{
    /// <summary>
    /// Writes <paramref name="statusCode"/> and a failed <see cref="ApiResponse{T}"/>
    /// envelope containing <paramref name="message"/> to the response.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="statusCode">The HTTP status code to set.</param>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task SendApiErrorAsync(this HttpContext httpContext, int statusCode, string message, CancellationToken ct)
    {
        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error(message), ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/unit/server/unit.server.csproj -- --treenode-filter "*EndpointErrorExtensions*"`
Expected: PASS (4 cases).

---

### Task 2: Wire-format regression guard (before any replacement)

**Files:**
- Test: `test/functional/web/Endpoints/Web/ApiErrorEnvelopeRegressionTests.cs` (create)

**Interfaces:**
- Consumes: `FunctionalTestFactory` from `test/shared`.
- Produces: a pinned wire-format assertion later tasks must keep green.

- [ ] **Step 1: Write a functional test that pins the current error envelope**

Pick one currently hand-written error path with a stable trigger — `POST /v1/api/billing/cancel` with billing disabled returns 404 `"Billing is not enabled"` (`CancelSubscriptionEndpoint.cs:80-87`). Assert status code AND payload:

```csharp
[Test]
public async Task ErrorResponses_KeepApiResponseEnvelopeShape()
{
    using FunctionalTestFactory factory = new();   // billing disabled by default in functional config
    HttpClient client = await factory.CreateAuthenticatedTenantAdminClientAsync();

    HttpResponseMessage response = await client.PostAsync("/v1/api/billing/cancel", null);
    string json = await response.Content.ReadAsStringAsync();
    using JsonDocument doc = JsonDocument.Parse(json);

    await Assert.That((int)response.StatusCode).IsEqualTo(404);
    await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
    await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("Billing is not enabled");
}
```

Adjust the client-construction call to whatever `FunctionalTestFactory` actually exposes (see existing billing endpoint tests in `test/functional/web/Endpoints/Web/` for the established pattern) — but keep the raw-`JsonDocument` assertions: the point is pinning the JSON property names, not round-tripping through `ApiResponse<T>`.

- [ ] **Step 2: Run it — must pass against the UNCHANGED endpoints**

Run: `dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*ApiErrorEnvelopeRegression*"`
Expected: PASS. This proves the pre-replacement shape; it must still pass after every batch below.

---

### Task 3: Mechanical replacement, batch by folder

**Files:**
- Modify: all 68 files under `src/server/Endpoints/` containing the idiom (discover per batch below)

**Interfaces:**
- Consumes: `SendApiErrorAsync` from Task 1.
- Produces: nothing new.

The pattern to replace (real example from `CancelSubscriptionEndpoint.cs:89-96`):

```csharp
// BEFORE
if (tenantId is null)
{
    HttpContext.Response.StatusCode = 401;
    await HttpContext.Response.WriteAsJsonAsync(ApiResponse<CancelSubscriptionResponse>.Error("Unauthorized"), ct);

    return;
}

// AFTER
if (tenantId is null)
{
    await HttpContext.SendApiErrorAsync(401, "Unauthorized", ct);

    return;
}
```

Rules:
- Preserve the status code and message string **exactly** — do not "improve" messages.
- If a block passes an `errors` list to `Error(message, errors)` (rare), leave that block alone and note it in the task summary — the helper only covers the message-only form.
- Add `using Framlux.FleetManagement.Server.Endpoints;` where the file doesn't already have it (alphabetical order).
- Do not touch `Send.OkAsync(...)` success paths.

- [ ] **Step 1: Batch A — `Endpoints/Web/Billing/`**

```bash
/usr/bin/grep -rln "Response.WriteAsJsonAsync" src/server/Endpoints/Web/Billing --include='*.cs'
```

Replace every occurrence in the listed files. Then:

```bash
dotnet build machine-info.slnx
dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*Billing*"
dotnet run --project test/functional/web/functional.web.csproj -- --treenode-filter "*ApiErrorEnvelopeRegression*"
```

Expected: 0 warnings, all pass.

- [ ] **Step 2: Batch B — `Endpoints/Web/Alerts/` and `Endpoints/Web/Integrations/`** (same procedure, filter `"*Alert*"` / `"*Integration*"`)

- [ ] **Step 3: Batch C — `Endpoints/Web/Machines/` and `Endpoints/Web/Invitations/`** (filters `"*Machine*"` / `"*Member*"` / `"*Invitation*"`)

- [ ] **Step 4: Batch D — everything remaining under `Endpoints/`**

```bash
/usr/bin/grep -rln "Response.WriteAsJsonAsync" src/server/Endpoints --include='*.cs'
```

Replace the remainder, then run the **full** functional and unit suites:

```bash
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/unit/server/unit.server.csproj
```

---

### Task 4: Sweep and lock in

- [ ] **Step 1: Verify no stragglers**

```bash
/usr/bin/grep -rn "Response.WriteAsJsonAsync" src/server --include='*.cs'
```

Expected: hits only in `EndpointErrorExtensions.cs`, `ProSubscriptionPreProcessor.cs`, and `SubscriptionStatusPreProcessor.cs` (the pre-processors migrate in plan 3). Anything else is a missed site — fix it.

- [ ] **Step 2: Full verification**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
```

Expected: 0 warnings, all pass.

---

## Exit criteria

1. `grep -rn "Response.WriteAsJsonAsync" src/server/Endpoints --include='*.cs'` → 0 hits.
2. `EndpointErrorExtensionsTests` (4 cases) and `ApiErrorEnvelopeRegressionTests` pass.
3. Full functional/web suite passes with **no assertion changes** in existing tests — the
   wire format is provably unchanged.
4. `dotnet build machine-info.slnx` — 0 errors, 0 warnings.
5. ~550–650 LOC removed from `src/server/Endpoints/`.
