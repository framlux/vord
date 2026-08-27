# Self-Hosted Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace three drifting inferred signals (`Billing:Enabled`, `PUBLIC_BILLING_URL`, empty `Resend:ApiKey`) with one explicit `Deployment:SelfHosted` flag that alone decides SaaS-versus-self-hosted behaviour, and unlock the full product for self-hosters.

**Architecture:** A `DeploymentOptions`/`DeploymentMode` pair becomes the single mode switch. `Billing:Enabled` is deleted and every consumer reads `DeploymentMode.IsSaas`. Self-hosted entitlement is delivered by a `SelfHostedSubscriptionService` decorator implementing all eleven `ISubscriptionService` members. Email becomes an `Email:*` section with the provider chosen by mode — Resend in SaaS (hard-required), MailKit SMTP in self-hosted (optional, falling back to a no-op).

**Tech Stack:** .NET 10, FastEndpoints, LinqToDB, Hangfire, TUnit, SvelteKit 5 / Skeleton v3, MailKit (new), Kustomize/ArgoCD.

**Spec:** `docs/specs/2026-08-23-self-hosted-mode-design.md` — read it before starting. It records why each decision was made and several corrections found during cross-check.

## Global Constraints

- **Repository must build with no credentials.** `nuget.config` lists nuget.org only. No package from an authenticated feed. No workflow may read from `framlux/vord-internal`.
- **No `var`** — explicit types everywhere (error-level in `.editorconfig`).
- **File-scoped namespaces**, Allman braces, private fields `_camelCase`, no `this.` qualifier.
- **XML doc comments required on all public members** (CS1591 is an error via `TreatWarningsAsErrors`).
- **Never use `!boolean`** — write `if (x == false)`. Never write Yoda conditions (`if (false == x)`); the variable goes first.
- **Blank line before every `return`** except when the preceding line is a comment. Blank line at end of file.
- **One type per file.** FastEndpoints Request/Response types may share the endpoint's file.
- **`using` statements in alphabetical order.** Spaces, not tabs. Timestamps serialized ISO 8601.
- **Every file starts with the three-line licence header** — copy it verbatim from any neighbouring file.
- **Comments describe intent in natural language.** Never reference task numbers, plan numbers, phases, or review IDs in code or comments.
- **Commit messages must not contain AI attribution** — no `Co-Authored-By`, no generated-with footer.
- **Tests:** TUnit, run via `dotnet run` — never `dotnet test`. Test intent, not coverage. Every task covers happy path, error cases, boundary values and null inputs. Target >80% line and branch coverage on new code.
- **TUnit stale-result gotcha:** `dotnet run --no-build` can replay a previous run's results. If output looks impossible, rebuild with `dotnet build --no-incremental` and run the compiled executable directly.
- **When you change a service's constructor, update every test that constructs or substitutes it** before running the suite.

**Test commands used throughout:**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*TestName*"
dotnet run --project test/unit/server/unit.server.csproj --treenode-filter "*TestName*"
dotnet run --project test/functional/web/functional.web.csproj --treenode-filter "*TestName*"
dotnet run --project test/functional/grpc/functional.grpc.csproj
pnpm -C src/web test
pnpm -C src/web check
```

---

## File Structure

**New files (vord):**

| Path | Responsibility |
| --- | --- |
| `src/services.core/Options/DeploymentOptions.cs` | The bound `Deployment:SelfHosted` value |
| `src/services.core/Options/DeploymentOptionsValidator.cs` | Cross-section startup validation |
| `src/services.core/Services/Deployment/DeploymentMode.cs` | Singleton read surface (`IsSelfHosted` / `IsSaas`) |
| `src/services.core/Services/Billing/SelfHostedSubscriptionService.cs` | Eleven-member entitlement decorator |
| `src/services.core/Options/EmailOptions.cs` | `Email:*` root |
| `src/services.core/Options/ResendEmailOptions.cs` | `Email:Resend:*` |
| `src/services.core/Options/SmtpEmailOptions.cs` | `Email:Smtp:*` |
| `src/services.core/Options/EmailOptionsValidator.cs` | Mode-aware email validation |
| `src/services.core/Services/Notifications/EmailTemplates.cs` | Invitation HTML, transport-independent |
| `src/services.core/Services/Notifications/SmtpEmailService.cs` | MailKit provider |
| `src/services.core/Services/Notifications/NoOpEmailService.cs` | Returns `Skipped` |
| `src/services.core/Models/Users/DeploymentDto.cs` | `{ selfHosted }` on the `/auth/me` payload |

**Deleted:** `src/services.core/Services/Billing/BillingStatus.cs`, `src/services.core/Options/ResendOptions.cs`, `src/services.core/Options/ResendOptionsValidator.cs`, `test/unit/services.core/Options/ResendOptionsValidatorTests.cs`, `test/unit/services.core/Services/Billing/BillingStatusTests.cs`.

**Renamed (Task 2):** `test/shared/BillingDisabledTestFactory.cs` → `SelfHostedTestFactory.cs`, and `test/functional/web/.../BillingDisabledEndpointTests.cs` → `SelfHostedEndpointTests.cs`. Both key on `Billing:Enabled`, which ceases to exist after Task 2; left alone they run in the wrong mode and fail.

**Modified (vord):** `ServiceCollectionExtensions.cs`, `BillingOptions.cs`, `BillingOptionsValidator.cs`, `server/Program.cs`, `services.worker/Program.cs`, `RecurringJobRegistry.cs`, `ResendEmailService.cs`, `UserDto.cs`, `AuthMeEndpoint.cs`, the six `BillingStatus` consumers, `appsettings.json` (server + worker), `deployment/server/docker/docker-compose.yml`, `CLAUDE.md`, plus web files listed in Task 6.

**Modified (stack):** `clusters/prod/apps/vord-platform/base/kustomization.yaml` and the four `fleet/*/deployment.yaml` image tags.

---

### Task 1: Deployment flag, validator and mode singleton

**Files:**
- Create: `src/services.core/Options/DeploymentOptions.cs`
- Create: `src/services.core/Options/DeploymentOptionsValidator.cs`
- Create: `src/services.core/Services/Deployment/DeploymentMode.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreOptions`, around line 67)
- Test: `test/unit/services.core/Options/DeploymentOptionsValidatorTests.cs`
- Test: `test/unit/services.core/Deployment/DeploymentModeTests.cs`

**Interfaces:**
- Produces: `DeploymentOptions { bool SelfHosted }`; `DeploymentMode { bool IsSelfHosted; bool IsSaas }` (singleton, constructor takes `IOptions<DeploymentOptions>`); `DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>` (constructor takes `IOptions<BillingOptions>` and `IConfiguration`).
- Consumes: nothing.

Note: the validator needs to read `InternalGrpc:Enabled`, which is bound in the *server* project (`Framlux.FleetManagement.Server.Options.InternalGrpcOptions`) and is not visible from `services.core`. Read it from `IConfiguration` directly rather than moving the options class — moving it would drag server-only certificate concerns into the shared library.

- [ ] **Step 1: Write the failing validator tests**

Create `test/unit/services.core/Options/DeploymentOptionsValidatorTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="DeploymentOptionsValidator"/>. The deployment mode is the single switch
/// between SaaS and self-hosted, so a configuration that contradicts the declared mode must stop
/// the process rather than degrade into the other mode silently.
/// </summary>
public sealed class DeploymentOptionsValidatorTests
{
    private static DeploymentOptionsValidator CreateValidator(
        BillingOptions billing,
        bool internalGrpcEnabled)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalGrpc:Enabled"] = internalGrpcEnabled ? "true" : "false",
            })
            .Build();

        return new DeploymentOptionsValidator(Options.Create(billing), configuration);
    }

    /// <summary>
    /// The zero-configuration case: a fresh clone with nothing set is a valid self-hosted install.
    /// </summary>
    [Test]
    public async Task Validate_DefaultOptions_IsSelfHostedAndSucceeds()
    {
        DeploymentOptions options = new();

        await Assert.That(options.SelfHosted).IsTrue();

        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: false)
            .Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// SaaS without a billing endpoint is not runnable — the failure would otherwise surface as a
    /// gRPC dial error on the first customer checkout.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithoutBillingGrpcUrl_Fails()
    {
        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: false)
            .Validate(null, new DeploymentOptions { SelfHosted = false });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Billing:GrpcUrl");
    }

    /// <summary>
    /// SaaS with a billing endpoint is the production configuration.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithBillingGrpcUrl_Succeeds()
    {
        BillingOptions billing = new() { GrpcUrl = "https://billing-api.internal:12237" };

        ValidateOptionsResult result = CreateValidator(billing, internalGrpcEnabled: true)
            .Validate(null, new DeploymentOptions { SelfHosted = false });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// The mutual-TLS control-plane listener serves only billing and fleet-admin, neither of which
    /// is mapped in self-hosted, so an enabled listener there is a misconfiguration.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithInternalGrpcEnabled_Fails()
    {
        ValidateOptionsResult result = CreateValidator(new BillingOptions(), internalGrpcEnabled: true)
            .Validate(null, new DeploymentOptions { SelfHosted = true });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("InternalGrpc:Enabled");
    }

    /// <summary>
    /// A leftover Billing section in self-hosted is inert, not fatal — flipping modes to test must
    /// not require gutting the configuration file.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithPopulatedBillingSection_Succeeds()
    {
        BillingOptions billing = new() { GrpcUrl = "https://billing-api.internal:12237" };

        ValidateOptionsResult result = CreateValidator(billing, internalGrpcEnabled: false)
            .Validate(null, new DeploymentOptions { SelfHosted = true });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Null options is a programming error, not a configuration error.
    /// </summary>
    [Test]
    public async Task Validate_NullOptions_Throws()
    {
        DeploymentOptionsValidator validator = CreateValidator(new BillingOptions(), internalGrpcEnabled: false);

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }
}
```

- [ ] **Step 2: Write the failing mode tests**

Create `test/unit/services.core/Deployment/DeploymentModeTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Deployment;

/// <summary>
/// Tests for <see cref="DeploymentMode"/>. Every SaaS-only subsystem keys off this one value, so
/// the two properties must be exact negations with no third state.
/// </summary>
public sealed class DeploymentModeTests
{
    [Test]
    public async Task IsSelfHosted_WhenConfiguredSelfHosted_IsTrueAndIsSaasIsFalse()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = true }));

        await Assert.That(mode.IsSelfHosted).IsTrue();
        await Assert.That(mode.IsSaas).IsFalse();
    }

    [Test]
    public async Task IsSaas_WhenConfiguredSaas_IsTrueAndIsSelfHostedIsFalse()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = false }));

        await Assert.That(mode.IsSaas).IsTrue();
        await Assert.That(mode.IsSelfHosted).IsFalse();
    }

    [Test]
    public async Task Constructor_DefaultOptions_IsSelfHosted()
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions()));

        await Assert.That(mode.IsSelfHosted).IsTrue();
    }
}
```

- [ ] **Step 3: Run both test files to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*DeploymentOptionsValidatorTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*DeploymentModeTests*"
```

Expected: compile failure — `DeploymentOptions`, `DeploymentOptionsValidator` and `DeploymentMode` do not exist.

- [ ] **Step 4: Create `DeploymentOptions`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Selects which of the two supported deployment shapes this process is running as. This is the
/// single switch: billing, the internal control plane, entitlement limits and the email provider
/// all derive from it, and nothing else may reintroduce a second, independently settable signal.
/// </summary>
public sealed class DeploymentOptions
{
    /// <summary>
    /// Whether this is a self-hosted deployment. Defaults to true so a clone of this repository
    /// runs correctly with no configuration at all; the hosted SaaS deployment is the one that
    /// opts out explicitly.
    /// </summary>
    public bool SelfHosted { get; set; } = true;
}
```

- [ ] **Step 5: Create `DeploymentOptionsValidator`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates that the rest of the configuration agrees with the declared deployment mode. A
/// deployment that declares one mode while configuring the other would otherwise start and behave
/// as the mode nobody intended, which is the failure this whole flag exists to remove.
/// </summary>
public sealed class DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>
{
    private readonly IOptions<BillingOptions> _billingOptions;
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates a new instance of the <see cref="DeploymentOptionsValidator"/> class.
    /// </summary>
    /// <param name="billingOptions">The bound billing configuration.</param>
    /// <param name="configuration">
    /// Raw configuration, used to read InternalGrpc:Enabled. That section is bound in the server
    /// project because its remaining fields are certificate paths that only the server uses, so it
    /// is not resolvable as typed options from this shared library.
    /// </param>
    public DeploymentOptionsValidator(IOptions<BillingOptions> billingOptions, IConfiguration configuration)
    {
        _billingOptions = billingOptions;
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.SelfHosted == false)
        {
            if (string.IsNullOrWhiteSpace(_billingOptions.Value.GrpcUrl))
            {
                failures.Add(
                    "Billing:GrpcUrl is required when Deployment:SelfHosted is false. A SaaS deployment without a reachable billing API cannot complete a checkout.");
            }
        }
        else
        {
            bool internalGrpcEnabled = _configuration.GetValue<bool>("InternalGrpc:Enabled");
            if (internalGrpcEnabled)
            {
                failures.Add(
                    "InternalGrpc:Enabled must be false when Deployment:SelfHosted is true. The mutual-TLS control plane serves only BillingGateway and FleetAdmin, neither of which is mapped in a self-hosted deployment.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 6: Create `DeploymentMode`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Deployment;

/// <summary>
/// Singleton read surface for the deployment mode. Consumers ask this rather than reading
/// configuration so there is exactly one place the mode is interpreted. Replaces the former
/// BillingStatus, which asked the same question through a billing-shaped proxy.
/// </summary>
public sealed class DeploymentMode
{
    /// <summary>
    /// Whether this process is running as a self-hosted deployment: no billing, no internal
    /// control plane, no tier limits, SMTP email.
    /// </summary>
    public bool IsSelfHosted { get; }

    /// <summary>
    /// Whether this process is running as the hosted SaaS deployment. The exact negation of
    /// <see cref="IsSelfHosted"/>; there is deliberately no third state.
    /// </summary>
    public bool IsSaas => IsSelfHosted == false;

    /// <summary>
    /// Creates a new instance of the <see cref="DeploymentMode"/> class.
    /// </summary>
    /// <param name="deploymentOptions">The bound deployment configuration.</param>
    public DeploymentMode(IOptions<DeploymentOptions> deploymentOptions)
    {
        ArgumentNullException.ThrowIfNull(deploymentOptions);

        IsSelfHosted = deploymentOptions.Value.SelfHosted;
    }
}
```

- [ ] **Step 7: Bind the options and register the singleton**

In `src/services.core/Extensions/ServiceCollectionExtensions.cs`, inside `AddCoreOptions`, immediately **before** the existing `BillingOptions` block at line 67:

```csharp
        services.AddOptions<DeploymentOptions>()
            .Bind(configuration.GetSection("Deployment"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeploymentOptions>, DeploymentOptionsValidator>();
        services.AddSingleton<DeploymentMode>();
```

Add `using Framlux.FleetManagement.Services.Core.Deployment;` to the file's using block, keeping alphabetical order (it goes after `...Core.DataExport;`).

- [ ] **Step 8: Run both test files to verify they pass**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*DeploymentOptionsValidatorTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*DeploymentModeTests*"
```

Expected: all nine tests PASS.

- [ ] **Step 9: Build the solution**

```bash
dotnet build machine-info.slnx
```

Expected: zero errors, zero warnings.

- [ ] **Step 10: Commit**

```bash
git add src/services.core/Options/DeploymentOptions.cs \
        src/services.core/Options/DeploymentOptionsValidator.cs \
        src/services.core/Services/Deployment/DeploymentMode.cs \
        src/services.core/Extensions/ServiceCollectionExtensions.cs \
        test/unit/services.core/Options/DeploymentOptionsValidatorTests.cs \
        test/unit/services.core/Deployment/DeploymentModeTests.cs
git commit -m "feat: add explicit Deployment:SelfHosted mode flag"
```

---

### Task 2: Delete `Billing:Enabled` and route every consumer through `DeploymentMode`

**Files:**
- Delete: `src/services.core/Services/Billing/BillingStatus.cs`
- Modify: `src/services.core/Options/BillingOptions.cs` (remove `Enabled`)
- Modify: `src/services.core/Options/BillingOptionsValidator.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreServices`, `AddHangfireJobTypes`, `AddBackgroundWorkers`)
- Modify: `src/services.core/Hangfire/RecurringJobRegistry.cs`
- Modify: `src/server/Program.cs` (lines 98–109, 283–291, 476–493)
- Modify: `src/services.worker/Program.cs` (lines ~40, ~52, ~55, ~80)
- Modify: `src/server/Endpoints/Web/Admin/UpdateAdminSettingsEndpoint.cs`
- Modify: `src/server/Endpoints/Web/Billing/BillingEndpointGuards.cs`
- Modify: `src/server/Endpoints/Web/Billing/{Cancel,Downgrade,Reactivate,Resume}SubscriptionEndpoint.cs`
- Modify: `src/server/appsettings.json`, `src/services.worker/appsettings.json`
- Test: `test/unit/services.core/Options/BillingOptionsValidatorTests.cs` (existing — update)

**Interfaces:**
- Consumes: `DeploymentMode` from Task 1.
- Produces: `AddCoreServices(IServiceCollection, DeploymentMode, ObjectStorageOptions, BillingOptions)`; `AddHangfireJobTypes(IServiceCollection, bool isSaas, bool objectStorageEnabled)`; `AddBackgroundWorkers(IServiceCollection, bool isSaas, ObjectStorageOptions, IConfiguration)`; `RecurringJobRegistry.RegisterAll(IRecurringJobManager, bool isSaas, bool objectStorageEnabled)`.

`AddCoreServices` takes `DeploymentMode` directly rather than resolving it from the container, because it runs before the container is built. Both `Program.cs` files construct one from the bound options for this purpose, and the container registration from Task 1 supplies the same value to runtime consumers.

**Note for later tasks:** Task 4 adds a fifth parameter to `AddCoreServices`, giving a final signature of `AddCoreServices(IServiceCollection, DeploymentMode, ObjectStorageOptions, EmailOptions, BillingOptions)`. Implement the four-parameter form here; Task 4 extends it.

- [ ] **Step 1: Update the existing `BillingOptionsValidator` tests**

Open `test/unit/services.core/Options/BillingOptionsValidatorTests.cs`. Delete every test that sets `Enabled`. Replace the file's test bodies so the remaining rule is only the certificate pairing:

```csharp
    /// <summary>
    /// A certificate without its key would silently fall back to an unauthenticated channel that
    /// the billing API then rejects, so the pair must be configured together or not at all.
    /// </summary>
    [Test]
    public async Task Validate_CertificateWithoutKey_Fails()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificatePath = "/tls/internal-client/tls.crt",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("ClientCertificateKeyPath");
    }

    /// <summary>
    /// A key without its certificate is the mirror-image misconfiguration.
    /// </summary>
    [Test]
    public async Task Validate_KeyWithoutCertificate_Fails()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificateKeyPath = "/tls/internal-client/tls.key",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("ClientCertificatePath");
    }

    /// <summary>
    /// Both halves present is the production configuration.
    /// </summary>
    [Test]
    public async Task Validate_CertificateAndKeyTogether_Succeeds()
    {
        BillingOptions options = new()
        {
            GrpcUrl = "https://billing-api.internal:12237",
            ClientCertificatePath = "/tls/internal-client/tls.crt",
            ClientCertificateKeyPath = "/tls/internal-client/tls.key",
        };

        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// An empty section is valid on its own; whether a billing endpoint is required is the
    /// deployment validator's decision, not this one's.
    /// </summary>
    [Test]
    public async Task Validate_EmptyOptions_Succeeds()
    {
        ValidateOptionsResult result = new BillingOptionsValidator().Validate(null, new BillingOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*BillingOptionsValidatorTests*"
```

Expected: compile failure or assertion failure — `BillingOptions.Enabled` still exists and the validator still enforces it.

- [ ] **Step 3: Remove `Enabled` from `BillingOptions` and its validator**

In `src/services.core/Options/BillingOptions.cs`, delete the `Enabled` property and its XML doc. Update the class summary to: `/// Configuration options for reaching the SaaS billing API. Ignored entirely when Deployment:SelfHosted is true.`

In `src/services.core/Options/BillingOptionsValidator.cs`, delete this block:

```csharp
        if (options.Enabled && string.IsNullOrWhiteSpace(options.GrpcUrl))
        {
            return ValidateOptionsResult.Fail("Billing:GrpcUrl is required when Billing:Enabled is true.");
        }
```

Update the class summary to say the GrpcUrl requirement now lives in `DeploymentOptionsValidator`.

- [ ] **Step 4: Delete `BillingStatus` and repoint its six consumers**

```bash
git rm src/services.core/Services/Billing/BillingStatus.cs
```

In each of the six files below, replace the `BillingStatus _billingStatus` field, its constructor parameter, and its usage:

- `src/server/Endpoints/Web/Admin/UpdateAdminSettingsEndpoint.cs`
- `src/server/Endpoints/Web/Billing/BillingEndpointGuards.cs`
- `src/server/Endpoints/Web/Billing/CancelSubscriptionEndpoint.cs`
- `src/server/Endpoints/Web/Billing/DowngradeSubscriptionEndpoint.cs`
- `src/server/Endpoints/Web/Billing/ReactivateSubscriptionEndpoint.cs`
- `src/server/Endpoints/Web/Billing/ResumeSubscriptionEndpoint.cs`

The mechanical substitution is:

| Before | After |
| --- | --- |
| `using Framlux.FleetManagement.Services.Core.Billing;` | `using Framlux.FleetManagement.Services.Core.Deployment;` (keep alphabetical order; drop the Billing using only if nothing else in the file needs it) |
| `private readonly BillingStatus _billingStatus;` | `private readonly DeploymentMode _deploymentMode;` |
| `BillingStatus billingStatus` (ctor param) | `DeploymentMode deploymentMode` |
| `_billingStatus = billingStatus;` | `_deploymentMode = deploymentMode;` |
| `_billingStatus.IsEnabled` | `_deploymentMode.IsSaas` |

In `UpdateAdminSettingsEndpoint.cs` the guard at line 43 reads `if (_billingStatus.IsEnabled)`. It becomes `if (_deploymentMode.IsSaas)`. Update the class summary from "Only available when billing is disabled" to "Only available in a self-hosted deployment; in SaaS these settings are managed from the internal admin application."

- [ ] **Step 5: Change the three registration signatures**

In `src/services.core/Extensions/ServiceCollectionExtensions.cs`:

Change `AddCoreServices` from `(this IServiceCollection services, BillingOptions billingOpts, ObjectStorageOptions objectStorageOpts)` to `(this IServiceCollection services, DeploymentMode deploymentMode, ObjectStorageOptions objectStorageOpts, BillingOptions billingOpts)`. Add `ArgumentNullException.ThrowIfNull(deploymentMode);` as the first statement. Delete the `services.AddSingleton<BillingStatus>();` line at 327. Change `if (billingOpts.Enabled)` at line 329 to `if (deploymentMode.IsSaas)`.

Change `AddHangfireJobTypes`'s `bool billingEnabled` parameter to `bool isSaas`, update its XML doc `<param>` accordingly, and change `if (billingEnabled)` at line 396 to `if (isSaas)`.

Change `AddBackgroundWorkers` from `(…, BillingOptions billingOpts, ObjectStorageOptions objectStorageOpts, IConfiguration configuration)` to `(…, bool isSaas, ObjectStorageOptions objectStorageOpts, IConfiguration configuration)`, and update its `AddHangfireJobTypes` call at line 442 to pass `isSaas: isSaas`.

In `src/services.core/Hangfire/RecurringJobRegistry.cs`, rename the `bool billingEnabled` parameter of `RegisterAll` to `bool isSaas`, update the XML doc, and update every internal use.

- [ ] **Step 6: Update `server/Program.cs`**

Replace lines 98–109 (the `billingOpts` read through the `internalGrpcListenerEnabled` block) with:

```csharp
BillingOptions billingOpts = builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new();
ObjectStorageOptions objectStorageOpts = builder.Configuration.GetSection("ObjectStorage").Get<ObjectStorageOptions>() ?? new();
DeploymentOptions deploymentOpts = builder.Configuration.GetSection("Deployment").Get<DeploymentOptions>() ?? new();
DeploymentMode deploymentMode = new(Microsoft.Extensions.Options.Options.Create(deploymentOpts));

// The internal billing and admin gRPC services listen on their own mutual-TLS port, and only in
// the hosted deployment. The agent port keeps its existing plain-text configuration — agents
// authenticate with an API key and have no client certificate, so requiring one there would
// refuse every machine in the fleet.
InternalGrpcOptions internalGrpcOpts = builder.Configuration.GetSection("InternalGrpc").Get<InternalGrpcOptions>() ?? new();
bool internalGrpcListenerEnabled = deploymentMode.IsSaas && internalGrpcOpts.Enabled;
if (internalGrpcListenerEnabled)
{
    builder.WebHost.ConfigureKestrel(options => InternalGrpcEndpoint.Configure(options, internalGrpcOpts));
}
```

At line 283 change `builder.Services.AddCoreServices(billingOpts, objectStorageOpts);` to `builder.Services.AddCoreServices(deploymentMode, objectStorageOpts, billingOpts);`.

At line 289 change `billingEnabled: billingOpts.Enabled,` to `isSaas: deploymentMode.IsSaas,`.

At line 476 change `if (billingOpts.Enabled)` to `if (deploymentMode.IsSaas)`.

Add `using Framlux.FleetManagement.Services.Core.Deployment;` in alphabetical position.

- [ ] **Step 7: Update `services.worker/Program.cs`**

After the `objectStorageOpts` read, add:

```csharp
DeploymentOptions deploymentOpts = builder.Configuration.GetSection("Deployment").Get<DeploymentOptions>() ?? new();
DeploymentMode deploymentMode = new(Microsoft.Extensions.Options.Options.Create(deploymentOpts));
```

Change `AddCoreServices(billingOpts, objectStorageOpts)` to `AddCoreServices(deploymentMode, objectStorageOpts, billingOpts)`; change `AddBackgroundWorkers(billingOpts, objectStorageOpts, builder.Configuration)` to `AddBackgroundWorkers(deploymentMode.IsSaas, objectStorageOpts, builder.Configuration)`; change `RegisterAll(recurringJobs, billingEnabled: billingOpts.Enabled, …)` to `RegisterAll(recurringJobs, isSaas: deploymentMode.IsSaas, …)`. Add the `Deployment` using in alphabetical position.

- [ ] **Step 8: Add the flag to both appsettings files**

In `src/server/appsettings.json` and `src/services.worker/appsettings.json`, add a top-level section after `"App"`:

```json
    "Deployment": {
        "SelfHosted": true
    },
```

This is the shipped default and documents the key's existence for self-hosters reading the file.

- [ ] **Step 9: Repoint the functional test hosts at the new flag — do this now, not later**

This step is mandatory in *this* task. `test/shared/FunctionalTestFactory.cs:120` currently selects SaaS behaviour solely through `Environment.SetEnvironmentVariable("Billing__Enabled", "true")`. Once Step 3 deletes that property, the variable binds to nothing and every functional host silently becomes **self-hosted** (the flag defaults to `true`). `BillingGatewayService` and `FleetAdminService` stop being mapped and the billing management endpoints start returning 404, so `test/functional/grpc/Endpoints/Grpc/BillingGatewayServiceTests.cs`, `FleetAdminServiceTests.cs` and the `test/functional/web/Endpoints/Web/Billing*` suites all fail — behaviourally, not at startup, so the failure looks like a product bug rather than a configuration gap.

In `FunctionalTestFactory.cs`, replace the `Billing__Enabled` line with:

```csharp
        // The functional hosts default to the hosted deployment so the existing suites keep
        // exercising the gated paths. Self-hosted coverage opts in via its own factory.
        Environment.SetEnvironmentVariable("Deployment__SelfHosted", "false");
```

Leave `Billing__GrpcUrl` in place — the SaaS deployment validator now requires it.

Also stage the email keys here, while they are still inert. Nothing binds an `Email` section until Task 4, so these are no-ops now — the same stage-while-inert reasoning the production rollout uses in Task 8. Staging them means Task 4 cannot land a commit whose functional suites refuse to start:

```csharp
        // Staged ahead of the email rework. Nothing binds an Email section yet, so these are inert
        // until that task lands, at which point the hosted host refuses to start without them.
        Environment.SetEnvironmentVariable("Email__FromEmail", "Framlux Vord <invitations@test.invalid>");
        Environment.SetEnvironmentVariable("Email__Resend__ApiKey", "re_functional_test");
```

In `test/shared/BillingDisabledTestFactory.cs:27`, the `["Billing:Enabled"] = "false"` override is likewise now a no-op. Rename the class to `SelfHostedTestFactory` and change the override to:

```csharp
                ["Deployment:SelfHosted"] = "true"
```

Update its class summary and **every** reference. There are five consumers, spanning both `functional/web` and `functional/grpc`:

- `test/functional/web/.../BillingDisabledEndpointTests.cs` — rename to `SelfHostedEndpointTests`; its 404 assertions are exactly the self-hosted assertions, so keep them. Task 5 extends this file rather than duplicating it.
- `test/functional/grpc/Endpoints/Grpc/FleetAdminServiceTests.cs:36`
- `test/functional/web/Endpoints/Web/AdminSettingsEndpointTests.cs` (six uses)
- `test/functional/web/Endpoints/Web/ApiErrorEnvelopeRegressionTests.cs:26`
- `test/functional/web/Endpoints/Web/BillingCatalogEndpointTests.cs:155`

Each asserts not-mapped / 404 / succeeds-when-disabled semantics that self-hosted reproduces exactly — for example `FleetAdmin_BillingDisabled_ServiceNotMapped` still holds, because the service is unmapped in self-hosted. Rename the *references*; do not change the assertions.

This factory uses `ConfigureAppConfiguration` with `AddInMemoryCollection`, which is per-host rather than process-global. That matters: TUnit runs tests in parallel, so a mode selected by environment variable would race across concurrently constructed hosts. **Any new mode-specific factory must use this in-memory pattern, never `SetEnvironmentVariable`.**

- [ ] **Step 10: Fix every remaining test that constructs the changed types**

```bash
grep -rln "BillingStatus\|billingOpts.Enabled\|Billing__Enabled\|Billing:Enabled\|billingEnabled:" test/
```

Every hit constructing one of the six endpoints, `AddCoreServices`, `AddHangfireJobTypes`, `AddBackgroundWorkers` or `RegisterAll` must move to the new signature. Where a test passed `new BillingOptions { Enabled = true }`, pass a `DeploymentMode` built from `new DeploymentOptions { SelfHosted = false }`. Named-argument call sites such as `test/unit/server/.../HangfireJobTypesTests.cs:57` (`billingEnabled:`) break at compile time; positional ones compile silently, so read each hit rather than trusting the build.

Delete `test/unit/services.core/Services/Billing/BillingStatusTests.cs` — it tests the class Step 4 removes.

Note that `BillingEndpointGuards.cs:23` takes `BillingStatus` as a **static method parameter**, not a constructor dependency, and is called from four endpoints (e.g. `CancelSubscriptionEndpoint.cs:72`). The substitution table above still applies to the type, but the edit shape is a parameter, not a field.

- [ ] **Step 11: Run the full unit and functional suites**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: all PASS, zero build warnings. A billing endpoint returning 404 where a test expects 200 means Step 9 was skipped or incomplete — the host is running self-hosted.

- [ ] **Step 12: Commit**

```bash
git add -A
git commit -m "refactor: replace Billing:Enabled with deployment mode as the single switch"
```

---

### Task 3: `SelfHostedSubscriptionService` entitlement decorator

**Files:**
- Create: `src/services.core/Services/Billing/SelfHostedSubscriptionService.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreServices`, line 277)
- Test: `test/unit/services.core/Services/Billing/SelfHostedSubscriptionServiceTests.cs`

**Interfaces:**
- Consumes: `DeploymentMode` (Task 1); `ISubscriptionService` (11 members, see `src/services.core/Services/Billing/ISubscriptionService.cs`); `TimeProvider` (already registered in `AddCoreServices`).
- Produces: `SelfHostedSubscriptionService : ISubscriptionService`, constructor `(ISubscriptionService inner, TimeProvider timeProvider)`.

**Read the spec's §4 before starting.** Getting a member wrong here fails *silently* — the self-hoster sees an unlocked UI while a limit still bites. In particular retention does **not** flow through `EffectiveLimits`.

**Two traps verified against source:**

1. `TenantSubscription` has **five `required` members** — `TenantId`, `Tier`, `Status`, `CreatedAt`, `UpdatedAt` (`src/database/Models/TenantSubscription.cs:27,39,45,65,71`). The synthetic object must set all five or it is CS9035. The timestamps come from an injected `TimeProvider`, never `DateTimeOffset.UtcNow` — this repo forbids wall-clock reads in testable code, and the synthetic row is serialized out of `GET /billing/subscription`, so a `MinValue` placeholder would be user-visible.
2. `IsIngestEligibleAsync` **delegates**. It is not an entitlement check: `SubscriptionService.cs:99` reads `Tenant.IsActive` first, and that is the tenant-deactivation and pending-deletion enforcement point. Answering `true` would let a deactivated tenant ingest forever. Delegating already gives full self-hosted ingest, because `IsIngestEligible` (`SubscriptionService.cs:83`) accepts any `Active`/`PastDue` subscription regardless of tier.

- [ ] **Step 1: Write the failing tests**

Create `test/unit/services.core/Services/Billing/SelfHostedSubscriptionServiceTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="SelfHostedSubscriptionService"/>. A self-hosted deployment has no tiers,
/// so every entitlement question must answer permissively. The failure mode this guards is a
/// member left delegating to the inner service: the user interface shows an unlocked product while
/// a Free-tier limit still refuses the operation, with no error anywhere to explain it.
/// </summary>
public sealed class SelfHostedSubscriptionServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static SelfHostedSubscriptionService CreateService(out ISubscriptionService inner)
    {
        inner = Substitute.For<ISubscriptionService>();

        return new SelfHostedSubscriptionService(inner, new FakeTimeProvider(FixedNow));
    }

    [Test]
    public async Task GetSubscriptionForTenantAsync_ReturnsSyntheticActiveTeamSubscription()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.TenantId).IsEqualTo(42);
        await inner.DidNotReceive().GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The synthetic row is serialized out of the subscription endpoint, so its timestamps are
    /// user-visible and must come from the injected clock rather than a placeholder.
    /// </summary>
    [Test]
    public async Task GetSubscriptionForTenantAsync_StampsTimestampsFromTimeProvider()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42);

        await Assert.That(result!.CreatedAt).IsEqualTo(FixedNow);
        await Assert.That(result.UpdatedAt).IsEqualTo(FixedNow);
    }

    [Test]
    public async Task GetEffectiveLimitsForTenantAsync_ReturnsMaxValueOnEveryField()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1);

        await Assert.That(limits.MachineLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.AlertRuleLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.WebhookLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.MemberLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.RetentionDays).IsEqualTo(RetentionClassPolicy.LongWindowDays);
    }

    [Test]
    public async Task CanCreateAlertRuleAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanCreateAlertRuleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanCreateAlertRuleAsync(1)).IsTrue();
    }

    [Test]
    public async Task CanCreateWebhookAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanCreateWebhookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanCreateWebhookAsync(1)).IsTrue();
    }

    [Test]
    public async Task CanAddMemberAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanAddMemberAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanAddMemberAsync(1)).IsTrue();
    }

    /// <summary>
    /// Ingest eligibility is not an entitlement question. The real implementation checks the
    /// tenant's active flag first, which is how deactivation and pending deletion stop telemetry,
    /// so overriding it would let a deactivated tenant ingest forever. A live self-hosted tenant is
    /// eligible anyway, because eligibility accepts any Active subscription regardless of tier.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_DelegatesSoTenantDeactivationStillBlocks()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.IsIngestEligibleAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        inner.IsIngestEligibleAsync(2, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsFalse();
        await Assert.That(await service.IsIngestEligibleAsync(2)).IsTrue();
    }

    /// <summary>
    /// Retention is the member most easily missed: it does not flow through EffectiveLimits, and a
    /// delegated implementation would compute one day from the real Free row, so ingested telemetry
    /// would be stamped Short and dropped after a day while the interface claimed Team.
    /// </summary>
    [Test]
    public async Task RetentionAccessors_ReturnLongWindowAndDoNotDelegate()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.GetRetentionDaysForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        inner.GetEffectiveRetentionDaysForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);

        await Assert.That(await service.GetRetentionDaysForTenantAsync(1))
            .IsEqualTo(RetentionClassPolicy.LongWindowDays);
        await Assert.That(await service.GetEffectiveRetentionDaysForTenantAsync(1))
            .IsEqualTo(RetentionClassPolicy.LongWindowDays);
    }

    /// <summary>
    /// The retention value must classify as Long, not merely be a large number. Long is the widest
    /// retention class the partitioning scheme has; there is no unlimited class.
    /// </summary>
    [Test]
    public async Task EffectiveRetention_ClassifiesAsLong()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        int days = await service.GetEffectiveRetentionDaysForTenantAsync(1);

        await Assert.That(RetentionClassPolicy.Classify(days)).IsEqualTo(RetentionClass.Long);
    }

    /// <summary>
    /// Machine counts carry no entitlement meaning, so they must report the truth. Faking them
    /// would corrupt the dashboard and the machine-registration path.
    /// </summary>
    [Test]
    public async Task MachineCountAccessors_DelegateToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>()).Returns(3);
        inner.GetBillableMachineCountAsync(7, SubscriptionTier.Team, Arg.Any<CancellationToken>()).Returns(3);

        await Assert.That(await service.GetMachineCountForTenantAsync(7)).IsEqualTo(3);
        await Assert.That(await service.GetBillableMachineCountAsync(7, SubscriptionTier.Team, default)).IsEqualTo(3);
        await inner.Received(1).GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetMachineCountAtDateAsync_DelegatesToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        DateTimeOffset when = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        inner.GetMachineCountAtDateAsync(7, when, Arg.Any<CancellationToken>()).Returns(5);

        await Assert.That(await service.GetMachineCountAtDateAsync(7, when)).IsEqualTo(5);
    }

    /// <summary>
    /// Row provisioning must still happen: the synthetic subscription is a read-side view, and
    /// downstream code still expects a real row to exist for the tenant.
    /// </summary>
    [Test]
    public async Task ProvisioningMembers_DelegateToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        TenantSubscription row = new()
        {
            TenantId = 9,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        };
        inner.ProvisionFreeSubscriptionAsync(9, Arg.Any<CancellationToken>()).Returns(row);

        await Assert.That(await service.ProvisionFreeSubscriptionAsync(9)).IsEqualTo(row);

        await service.EnsureSubscriptionExistsAsync(9);
        await inner.Received(1).EnsureSubscriptionExistsAsync(9, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        await Assert.That(() => new SelfHostedSubscriptionService(null!, new FakeTimeProvider(FixedNow)))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullTimeProvider_Throws()
    {
        ISubscriptionService inner = Substitute.For<ISubscriptionService>();

        await Assert.That(() => new SelfHostedSubscriptionService(inner, null!))
            .Throws<ArgumentNullException>();
    }
}
```

`FakeTimeProvider` is already available — `Microsoft.Extensions.TimeProvider.Testing` 10.7.0 is referenced at `test/unit/services.core/unit.services.core.csproj:16`, and the `Microsoft.Extensions.Time.Testing` namespace is already used elsewhere in this repo (for example `test/unit/server/Endpoints/Grpc/TelemetryServiceTests.cs:26`). No package change is needed. `FakeTimeProvider(DateTimeOffset)` pins the clock and does not auto-advance, so the timestamp assertions are exact.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*SelfHostedSubscriptionServiceTests*"
```

Expected: compile failure — `SelfHostedSubscriptionService` does not exist.

- [ ] **Step 3: Implement the decorator**

Create `src/services.core/Services/Billing/SelfHostedSubscriptionService.cs`. Confirm the exact member signatures against `ISubscriptionService.cs` as you write — all eleven must be present.

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Entitlement view for a self-hosted deployment, where there are no subscription tiers and
/// nothing is gated. Wraps the real subscription service and answers every entitlement question
/// permissively while delegating the questions that carry no entitlement meaning.
/// </summary>
/// <remarks>
/// <para>
/// The tenant's stored subscription row remains Free — this type does not write anything. It
/// reports a synthetic Team, Active subscription so the tier checks scattered across the endpoints
/// pass without each having to learn about deployment mode.
/// </para>
/// <para>
/// That synthetic tier does reach a write path: machine registration takes a Pro-or-Team branch
/// that calls IBillingApiClient.UpdateQuantityAsync. It is harmless only because a self-hosted
/// deployment resolves NoOpBillingApiClient, which does nothing and reports success. That no-op is
/// the invariant keeping this safe — not any routing rule — so any future write keyed on tier must
/// be checked against it.
/// </para>
/// <para>
/// One path deliberately escapes this decorator: RetentionReclassifyJob injects an uncached,
/// undecorated ISubscriptionRepository and would see the real Free row. It is dormant in
/// self-hosted because its only dispatch site is the SaaS-only FleetAdminService, and making it
/// reachable here would require revisiting this class.
/// </para>
/// <para>
/// Every member must be implemented explicitly. A member left delegating silently reintroduces a
/// Free-tier limit that the user interface will not reflect and no error will explain.
/// </para>
/// </remarks>
public sealed class SelfHostedSubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionService _inner;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="SelfHostedSubscriptionService"/> class.
    /// </summary>
    /// <param name="inner">The real subscription service, used for non-entitlement queries.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp the synthetic subscription. The synthetic row is serialized by the
    /// subscription endpoint, so its timestamps are user-visible and must be a real time.
    /// </param>
    public SelfHostedSubscriptionService(ISubscriptionService inner, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _inner = inner;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        TenantSubscription synthetic = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return Task.FromResult<TenantSubscription?>(synthetic);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately delegated. This is not an entitlement question: the real implementation checks
    /// the tenant's active flag first, which is how tenant deactivation and pending deletion stop
    /// telemetry within a single request. Answering permissively here would let a deactivated
    /// tenant ingest forever. A live self-hosted tenant is eligible anyway, because eligibility
    /// accepts any active subscription regardless of tier.
    /// </remarks>
    public Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.IsIngestEligibleAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.ProvisionFreeSubscriptionAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the widest retention class the partitioning scheme supports rather than a nominally
    /// unlimited value. There is no unlimited class; anything above the long window would be
    /// classified as Long anyway, so reporting Long keeps the stated retention honest.
    /// </remarks>
    public Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(RetentionClassPolicy.LongWindowDays);
    }

    /// <inheritdoc/>
    public Task<int> GetEffectiveRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(RetentionClassPolicy.LongWindowDays);
    }

    /// <inheritdoc/>
    public Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.GetMachineCountForTenantAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task EnsureSubscriptionExistsAsync(int tenantId, CancellationToken ct = default)
    {
        return _inner.EnsureSubscriptionExistsAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct = default)
    {
        return _inner.GetMachineCountAtDateAsync(tenantId, targetDate, ct);
    }

    /// <inheritdoc/>
    public Task<bool> CanCreateAlertRuleAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<EffectiveLimits> GetEffectiveLimitsForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        EffectiveLimits limits = new()
        {
            MachineLimit = int.MaxValue,
            RetentionDays = RetentionClassPolicy.LongWindowDays,
            AlertRuleLimit = int.MaxValue,
            WebhookLimit = int.MaxValue,
            MemberLimit = int.MaxValue,
        };

        return Task.FromResult(limits);
    }

    /// <inheritdoc/>
    public Task<bool> CanAddMemberAsync(int tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<int> GetBillableMachineCountAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        return _inner.GetBillableMachineCountAsync(tenantId, tier, ct);
    }
}
```

If `RetentionClassPolicy` is not in the `Framlux.FleetManagement.Database` namespace, correct the using to match `src/database/RetentionClassPolicy.cs`.

- [ ] **Step 4: Register the decorator**

In `AddCoreServices`, replace line 277 (`services.AddScoped<ISubscriptionService, SubscriptionService>();`) with:

```csharp
        // In a self-hosted deployment there are no tiers, so the real service is wrapped by one
        // that answers every entitlement question permissively. It is registered as a decorator
        // rather than as a set of branches inside the callers because the entitlement checks are
        // spread across roughly twenty endpoints, handlers and jobs.
        services.AddScoped<SubscriptionService>();
        if (deploymentMode.IsSelfHosted)
        {
            services.AddScoped<ISubscriptionService>(sp =>
                new SelfHostedSubscriptionService(
                    sp.GetRequiredService<SubscriptionService>(),
                    sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.AddScoped<ISubscriptionService>(sp => sp.GetRequiredService<SubscriptionService>());
        }
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*SelfHostedSubscriptionServiceTests*"
```

Expected: all tests PASS.

- [ ] **Step 6: Verify no interface member was missed**

```bash
grep -c "public Task" src/services.core/Services/Billing/SelfHostedSubscriptionService.cs
grep -c "    Task" src/services.core/Services/Billing/ISubscriptionService.cs
```

Expected: both print `13`. If they differ, a member is missing and will silently delegate.

- [ ] **Step 7: Build and run the full suites**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
```

Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git add src/services.core/Services/Billing/SelfHostedSubscriptionService.cs \
        src/services.core/Extensions/ServiceCollectionExtensions.cs \
        test/unit/services.core/Services/Billing/SelfHostedSubscriptionServiceTests.cs
git commit -m "feat: unlock all entitlements in self-hosted deployments"
```

---

### Task 4: Email provider selected by mode

**Files:**
- Create: `src/services.core/Options/EmailOptions.cs`, `ResendEmailOptions.cs`, `SmtpEmailOptions.cs`, `EmailOptionsValidator.cs`
- Create: `src/services.core/Services/Notifications/EmailTemplates.cs`, `SmtpEmailService.cs`, `NoOpEmailService.cs`
- Delete: `src/services.core/Options/ResendOptions.cs`, `ResendOptionsValidator.cs`, `test/unit/services.core/Options/ResendOptionsValidatorTests.cs`
- Modify: `src/services.core/Services/Notifications/ResendEmailService.cs`
- Modify: `src/services.core/Extensions/ServiceCollectionExtensions.cs` (`AddCoreOptions` lines 95–106, `AddCoreServices` line 289)
- Modify: `src/services.core/services.core.csproj` (add MailKit)
- Rewrite: `test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs`
- Modify: `test/unit/services.core/Extensions/ServiceCollectionExtensionsTests.cs` (lines ~55–95)
- Test: `test/unit/services.core/Options/EmailOptionsValidatorTests.cs`, `test/unit/services.core/Services/Notifications/EmailTemplatesTests.cs`, `NoOpEmailServiceTests.cs`

**Two existing test files assert behaviour this task deletes** and must be handled explicitly rather than mechanically migrated:

- `ResendEmailServiceTests.cs` constructs `ResendEmailService(httpClient, IOptions<ResendOptions>, logger)` throughout and has roughly six tests asserting `Skipped` when the key is absent, empty or whitespace (around lines 31, 46, 61, 287, 302). **Delete those tests** — the keyless path is now `NoOpEmailService`'s, covered by `NoOpEmailServiceTests`. Rewrite the remaining tests against `IOptions<EmailOptions>`.
- `ServiceCollectionExtensionsTests.cs` has three `AddCoreOptions_*LogsEmailDisabled*` tests covering the `PostConfigure` logging this task drops. **Delete them**; do not try to preserve the assertion.

**Interfaces:**
- Consumes: `DeploymentMode` (Task 1); `IEmailService`, `EmailDeliveryOutcome` (unchanged).
- Produces: `EmailOptions { string FromEmail; ResendEmailOptions Resend; SmtpEmailOptions Smtp }`; `EmailTemplates.RenderInvitation(string tenantName, string inviterName, string acceptUrl) → string`; `SmtpEmailService : IEmailService`; `NoOpEmailService : IEmailService`.

`IEmailService` and `EmailDeliveryOutcome` do **not** change. `AlertDeliveryService` and `SendInvitationEmailJob` need no edits. Alert HTML already lives in `AlertEmailContent.cs` and stays there — only the invitation HTML moves.

- [ ] **Step 1: Add MailKit**

In `src/services.core/services.core.csproj`, add to the `PackageReference` item group in alphabetical position:

```xml
    <PackageReference Include="MailKit" Version="4.14.0" />
```

Verify the version resolves from nuget.org only:

```bash
dotnet restore src/services.core/services.core.csproj
```

If 4.14.0 does not exist, use the latest 4.x and record the version you used.

- [ ] **Step 2: Write the failing option and template tests**

Create `test/unit/services.core/Options/EmailOptionsValidatorTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Validators;

/// <summary>
/// Tests for <see cref="EmailOptionsValidator"/>. Email is optional for a self-hosted deployment
/// and mandatory for the hosted one — treating a missing key as "email is off" in SaaS is exactly
/// the overloaded signal this design removes, so there it must stop the process.
/// </summary>
public sealed class EmailOptionsValidatorTests
{
    private static EmailOptionsValidator CreateValidator(bool selfHosted)
    {
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));

        return new EmailOptionsValidator(mode);
    }

    /// <summary>
    /// A self-hosted deployment with no email configured at all is supported: sends are skipped.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedWithNothingConfigured_Succeeds()
    {
        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, new EmailOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A self-hosted deployment with SMTP configured must also declare a sender address.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedSmtpHostWithoutFromEmail_Fails()
    {
        EmailOptions options = new() { Smtp = new SmtpEmailOptions { Host = "smtp.example.com" } };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:FromEmail");
    }

    [Test]
    public async Task Validate_SelfHostedSmtpFullyConfigured_Succeeds()
    {
        EmailOptions options = new()
        {
            FromEmail = "alerts@example.com",
            Smtp = new SmtpEmailOptions { Host = "smtp.example.com", Port = 587 },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Port zero would silently mean "provider default" in some clients and fail in others, so it
    /// is rejected rather than guessed at.
    /// </summary>
    [Test]
    public async Task Validate_SelfHostedSmtpPortOutOfRange_Fails()
    {
        EmailOptions options = new()
        {
            FromEmail = "alerts@example.com",
            Smtp = new SmtpEmailOptions { Host = "smtp.example.com", Port = 0 },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: true).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:Smtp:Port");
    }

    /// <summary>
    /// In the hosted deployment a missing Resend key is always a misconfiguration, never a
    /// deployment style, so it stops startup instead of silently disabling invitations.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithoutResendApiKey_Fails()
    {
        EmailOptions options = new() { FromEmail = "alerts@example.com" };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:Resend:ApiKey");
    }

    /// <summary>
    /// Resend rejects any send from an unverified address and the rejection is only visible in
    /// logs, so a missing sender is a startup failure too.
    /// </summary>
    [Test]
    public async Task Validate_SaasWithApiKeyButNoFromEmail_Fails()
    {
        EmailOptions options = new() { Resend = new ResendEmailOptions { ApiKey = "re_test" } };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Email:FromEmail");
    }

    [Test]
    public async Task Validate_SaasFullyConfigured_Succeeds()
    {
        EmailOptions options = new()
        {
            FromEmail = "Framlux Vord <invitations@outreach.framlux.io>",
            Resend = new ResendEmailOptions { ApiKey = "re_test" },
        };

        ValidateOptionsResult result = CreateValidator(selfHosted: false).Validate(null, options);

        await Assert.That(result.Succeeded).IsTrue();
    }

    [Test]
    public async Task Validate_NullOptions_Throws()
    {
        EmailOptionsValidator validator = CreateValidator(selfHosted: true);

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }
}
```

Create `test/unit/services.core/Services/Notifications/EmailTemplatesTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Services.Notifications;

/// <summary>
/// Tests for <see cref="EmailTemplates"/>. The invitation body used to live inside the Resend
/// transport; it is shared now, so both providers must render the same message and both must
/// encode caller-supplied names.
/// </summary>
public sealed class EmailTemplatesTests
{
    [Test]
    public async Task RenderInvitation_IncludesTenantInviterAndUrl()
    {
        string html = EmailTemplates.RenderInvitation("Acme Fleet", "Dana Reid", "https://app.example.com/accept?t=abc");

        await Assert.That(html).Contains("Acme Fleet");
        await Assert.That(html).Contains("Dana Reid");
        await Assert.That(html).Contains("https://app.example.com/accept?t=abc");
    }

    /// <summary>
    /// Tenant names are user-supplied, so an unencoded template would let a tenant name inject
    /// markup into an email sent to someone who is not yet a member.
    /// </summary>
    [Test]
    public async Task RenderInvitation_EncodesHtmlInTenantName()
    {
        string html = EmailTemplates.RenderInvitation("<script>alert(1)</script>", "Dana", "https://example.com");

        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html).Contains("&lt;script&gt;");
    }

    [Test]
    public async Task RenderInvitation_EncodesHtmlInInviterName()
    {
        string html = EmailTemplates.RenderInvitation("Acme", "<b>Dana</b>", "https://example.com");

        await Assert.That(html).DoesNotContain("<b>Dana</b>");
    }

    [Test]
    public async Task InvitationSubject_NamesTheTenant()
    {
        await Assert.That(EmailTemplates.InvitationSubject("Acme Fleet")).Contains("Acme Fleet");
    }
}
```

Create `test/unit/services.core/Services/Notifications/NoOpEmailServiceTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Services.Notifications;

/// <summary>
/// Tests for <see cref="NoOpEmailService"/>. Skipped is terminal success: callers must never
/// retry it, so this service must never report Failed.
/// </summary>
public sealed class NoOpEmailServiceTests
{
    [Test]
    public async Task SendInvitationEmailAsync_ReturnsSkipped()
    {
        NoOpEmailService service = new(NullLogger<NoOpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendInvitationEmailAsync(
            "a@example.com", "Acme", "Dana", "https://example.com", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }

    [Test]
    public async Task SendAlertEmailAsync_ReturnsSkipped()
    {
        NoOpEmailService service = new(NullLogger<NoOpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendAlertEmailAsync(
            "a@example.com", "subject", "<p>body</p>", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }
}
```

Add `using Microsoft.Extensions.Logging.Abstractions;` to that file if `NullLogger` is not already in the test project's `GlobalUsings.cs`.

- [ ] **Step 3: Run the three test files to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*EmailOptionsValidatorTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*EmailTemplatesTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*NoOpEmailServiceTests*"
```

Expected: compile failures — none of the new types exist.

- [ ] **Step 4: Create the three options classes**

`src/services.core/Options/ResendEmailOptions.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Settings for the Resend transport, used by the hosted deployment.
/// </summary>
public sealed class ResendEmailOptions
{
    /// <summary>
    /// The Resend API key. Required in a hosted deployment; ignored in a self-hosted one.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
```

`src/services.core/Options/SmtpEmailOptions.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Settings for the SMTP transport, used by self-hosted deployments. An empty host means email is
/// switched off, which is a supported configuration.
/// </summary>
public sealed class SmtpEmailOptions
{
    /// <summary>
    /// The SMTP server hostname. Empty disables email entirely.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The SMTP server port. Defaults to the submission port.
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// The username for SMTP authentication. Empty sends unauthenticated, which suits a local relay.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The password for SMTP authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether to upgrade the connection with STARTTLS. Defaults to true; set false only for a
    /// relay reachable exclusively over a trusted local network.
    /// </summary>
    public bool UseStartTls { get; set; } = true;
}
```

`src/services.core/Options/EmailOptions.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Email configuration. The transport is chosen by deployment mode rather than configured here:
/// the hosted deployment sends through Resend, a self-hosted one through SMTP.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>
    /// The sender address used by whichever transport is active. For Resend it must be on a domain
    /// verified in Resend, or every send is rejected.
    /// </summary>
    public string FromEmail { get; set; } = string.Empty;

    /// <summary>
    /// Resend transport settings, used in a hosted deployment.
    /// </summary>
    public ResendEmailOptions Resend { get; set; } = new();

    /// <summary>
    /// SMTP transport settings, used in a self-hosted deployment.
    /// </summary>
    public SmtpEmailOptions Smtp { get; set; } = new();
}
```

- [ ] **Step 5: Create `EmailOptionsValidator`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Validates email configuration against the deployment mode. The two modes have opposite rules:
/// self-hosted may run with no email at all, while the hosted deployment always sends invitations
/// and alerts, so a missing key there is a misconfiguration that must stop startup rather than
/// silently disable delivery.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    private readonly DeploymentMode _deploymentMode;

    /// <summary>
    /// Creates a new instance of the <see cref="EmailOptionsValidator"/> class.
    /// </summary>
    /// <param name="deploymentMode">The deployment mode this process is running as.</param>
    public EmailOptionsValidator(DeploymentMode deploymentMode)
    {
        _deploymentMode = deploymentMode;
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (_deploymentMode.IsSaas)
        {
            if (string.IsNullOrWhiteSpace(options.Resend.ApiKey))
            {
                failures.Add(
                    "Email:Resend:ApiKey is required when Deployment:SelfHosted is false. The hosted deployment always sends invitations and alerts, so a missing key is a misconfiguration rather than a deployment style.");
            }

            if (string.IsNullOrWhiteSpace(options.FromEmail))
            {
                failures.Add(
                    "Email:FromEmail is required when Deployment:SelfHosted is false. It must be an address on a domain verified in Resend, or every send is rejected.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.Smtp.Host) == false)
        {
            if (string.IsNullOrWhiteSpace(options.FromEmail))
            {
                failures.Add("Email:FromEmail is required when Email:Smtp:Host is configured.");
            }

            if ((options.Smtp.Port < 1) || (options.Smtp.Port > 65535))
            {
                failures.Add("Email:Smtp:Port must be between 1 and 65535.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 6: Create `EmailTemplates`**

Move the invitation HTML out of `ResendEmailService.cs:46` verbatim — do not restyle it.

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Renders email bodies independently of the transport that sends them, so the Resend and SMTP
/// providers deliver identical messages. Alert bodies are built by AlertEmailContent and passed in
/// already rendered; only invitations are composed here.
/// </summary>
public static class EmailTemplates
{
    /// <summary>
    /// Builds the subject line for a tenant invitation.
    /// </summary>
    /// <param name="tenantName">The tenant the recipient is being invited to.</param>
    /// <returns>The subject line.</returns>
    public static string InvitationSubject(string tenantName)
    {
        return $"You've been invited to join {tenantName} on Framlux Vord";
    }

    /// <summary>
    /// Builds the HTML body for a tenant invitation. Tenant and inviter names are supplied by
    /// users, so both are HTML-encoded before substitution.
    /// </summary>
    /// <param name="tenantName">The tenant the recipient is being invited to.</param>
    /// <param name="inviterName">The display name of the member who sent the invitation.</param>
    /// <param name="acceptUrl">The absolute URL that accepts the invitation.</param>
    /// <returns>The rendered HTML body.</returns>
    public static string RenderInvitation(string tenantName, string inviterName, string acceptUrl)
    {
        string encodedTenant = WebUtility.HtmlEncode(tenantName);
        string encodedInviter = WebUtility.HtmlEncode(inviterName);
        string encodedUrl = WebUtility.HtmlEncode(acceptUrl);

        return $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 560px; margin: 0 auto; padding: 40px 20px;">
                <h2 style="color: #1a1a1a; margin-bottom: 8px;">You've been invited to join {encodedTenant}</h2>
                <p style="color: #666; font-size: 15px; line-height: 1.5;">
                    {encodedInviter} has invited you to join <strong>{encodedTenant}</strong> on Framlux Vord.
                </p>
                <div style="margin: 32px 0;">
                    <a href="{encodedUrl}" style="display: inline-block; background-color: #6366f1; color: #ffffff; text-decoration: none; padding: 12px 32px; border-radius: 8px; font-weight: 600; font-size: 15px;">
                        Accept Invitation
                    </a>
                </div>
                <p style="color: #999; font-size: 13px; line-height: 1.5;">
                    This invitation expires in 7 days. If you did not expect this email, you can safely ignore it.
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 32px 0;" />
                <p style="color: #bbb; font-size: 12px;">Framlux Vord &mdash; Fleet Monitoring</p>
            </div>
            """;
    }
}
```

- [ ] **Step 7: Create `NoOpEmailService`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service used when a self-hosted deployment has configured no SMTP host. Every send
/// reports Skipped, which callers treat as terminal success — retrying could never help.
/// </summary>
public sealed class NoOpEmailService : IEmailService
{
    private readonly ILogger<NoOpEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="NoOpEmailService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public NoOpEmailService(ILogger<NoOpEmailService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        _logger.LogInformation("No email transport is configured, so the invitation to {Email} was not sent.", toEmail);

        return Task.FromResult(EmailDeliveryOutcome.Skipped);
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        _logger.LogInformation("No email transport is configured, so the alert email to {Email} was not sent.", toEmail);

        return Task.FromResult(EmailDeliveryOutcome.Skipped);
    }
}
```

- [ ] **Step 8: Create `SmtpEmailService`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Email service that delivers through an operator-supplied SMTP relay. This is the transport for
/// self-hosted deployments, where requiring an account with a hosted email provider would be a
/// barrier to running the product at all.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<SmtpEmailService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SmtpEmailService"/> class.
    /// </summary>
    /// <param name="emailOptions">The bound email configuration.</param>
    /// <param name="logger">The logger.</param>
    public SmtpEmailService(IOptions<EmailOptions> emailOptions, ILogger<SmtpEmailService> logger)
    {
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        return SendAsync(
            toEmail,
            EmailTemplates.InvitationSubject(tenantName),
            EmailTemplates.RenderInvitation(tenantName, inviterName, acceptUrl),
            ct);
    }

    /// <inheritdoc/>
    public Task<EmailDeliveryOutcome> SendAlertEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        return SendAsync(toEmail, subject, htmlBody, ct);
    }

    private async Task<EmailDeliveryOutcome> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        SmtpEmailOptions smtp = _emailOptions.Smtp;

        MimeMessage message = new();
        message.From.Add(MailboxAddress.Parse(_emailOptions.FromEmail));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        try
        {
            using SmtpClient client = new();

            SecureSocketOptions socketOptions = smtp.UseStartTls
                ? SecureSocketOptions.StartTls
                : SecureSocketOptions.None;

            await client.ConnectAsync(smtp.Host, smtp.Port, socketOptions, ct);

            if (string.IsNullOrWhiteSpace(smtp.Username) == false)
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, ct);
            }

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("Email sent to {Email} via SMTP host {Host}", toEmail, smtp.Host);

            return EmailDeliveryOutcome.Sent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email} via SMTP host {Host}", toEmail, smtp.Host);

            return EmailDeliveryOutcome.Failed;
        }
    }
}
```

Cancellation is rethrown rather than reported as `Failed`: a cancelled send is a shutdown, and recording it as a delivery failure would make Hangfire retry work that was never attempted.

- [ ] **Step 9: Repoint `ResendEmailService` at the new options**

In `src/services.core/Services/Notifications/ResendEmailService.cs`:

- Change the field from `ResendOptions _resendOptions` to `EmailOptions _emailOptions`, and the constructor parameter from `IOptions<ResendOptions>` to `IOptions<EmailOptions>`.
- Replace `string apiKey = _resendOptions.ApiKey;` with `string apiKey = _emailOptions.Resend.ApiKey;` in both methods, and `string fromEmail = _resendOptions.FromEmail;` with `string fromEmail = _emailOptions.FromEmail;`.
- **Delete both `if (string.IsNullOrWhiteSpace(apiKey))` early-return blocks** (lines ~38–43 and ~108–113). This service is now registered only when a key is present; the keyless case is `NoOpEmailService`'s job. Leaving them would silently reintroduce the overloaded signal.
- Replace the inline invitation HTML block with `string htmlBody = EmailTemplates.RenderInvitation(tenantName, inviterName, acceptUrl);` and the subject with `EmailTemplates.InvitationSubject(tenantName)`.
- Delete the now-unused `private static string HtmlEncode` helper.

- [ ] **Step 10: Delete the old options and register the new ones**

```bash
git rm src/services.core/Options/ResendOptions.cs \
       src/services.core/Options/ResendOptionsValidator.cs \
       test/unit/services.core/Options/ResendOptionsValidatorTests.cs
```

In `AddCoreOptions`, replace the whole `ResendOptions` block (lines 95–106) with:

```csharp
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection("Email"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
```

The `PostConfigure` logging is dropped: the provider registration below logs which transport was selected, which is the same information without the misleading "email is disabled" phrasing in a deployment where it is not.

In `AddCoreServices`, replace `services.AddHttpClient<IEmailService, ResendEmailService>();` (line 289) with:

```csharp
        // The transport follows the deployment mode. A hosted deployment always has a Resend key
        // (startup validation enforces it); a self-hosted one uses an operator-supplied SMTP relay,
        // or none at all, in which case every send reports Skipped.
        if (deploymentMode.IsSaas)
        {
            services.AddHttpClient<IEmailService, ResendEmailService>();
        }
        else if (string.IsNullOrWhiteSpace(emailOpts.Smtp.Host) == false)
        {
            services.AddSingleton<IEmailService, SmtpEmailService>();
        }
        else
        {
            services.AddSingleton<IEmailService, NoOpEmailService>();
        }
```

Add an `EmailOptions emailOpts` parameter to `AddCoreServices` (after `objectStorageOpts`) and pass it from both `Program.cs` files:

```csharp
EmailOptions emailOpts = builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new();
```

- [ ] **Step 11: Run the three test files to verify they pass**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*EmailOptionsValidatorTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*EmailTemplatesTests*"
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "*NoOpEmailServiceTests*"
```

Expected: all PASS.

- [ ] **Step 12: Update every remaining `Resend` reference**

```bash
grep -rn "ResendOptions\|Resend__\|\"Resend\"\|Resend:" src/ test/ --include="*.cs" --include="*.json"
```

Most hits move to the `Email` shape — but not all. The `ResendEmailServiceTests` no-key tests and the `AddCoreOptions_*LogsEmailDisabled*` tests are deletions, per this task's file list. Read each hit before editing it; a mechanical rename would resurrect the overloaded "no key means self-hosted" signal inside the test suite.

- [ ] **Step 13: Substitute `IEmailService` in the functional hosts**

Task 2 Step 9 staged `Email__Resend__ApiKey` in `FunctionalTestFactory`. As of this task that key is live, which means the SaaS functional hosts would resolve a real `ResendEmailService` and a test could make an actual HTTP call to Resend. Close that now, in the same commit that makes the key meaningful.

In `FunctionalTestFactory`'s service configuration, after the application's own registrations:

```csharp
        // The hosted host carries a real-looking API key so startup validation passes, which makes
        // the Resend transport live. Replace it for every functional test so no test can reach the
        // network; individual tests that assert on delivery still substitute their own.
        services.RemoveAll<IEmailService>();
        services.AddSingleton<IEmailService, NoOpEmailService>();
```

Add `using Microsoft.Extensions.DependencyInjection.Extensions;` if not present.

- [ ] **Step 14: Build and run every suite, including the functional ones**

The functional suites must run here, not later. This task makes a missing `Email:Resend:ApiKey` a hard startup failure in SaaS, and the functional hosts run in SaaS — so if Task 2 Step 9's staging was skipped, every functional host throws `OptionsValidationException` at build and this commit would be red.

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: all PASS, zero warnings. An `OptionsValidationException` naming `Email:Resend:ApiKey` means the Task 2 staging is missing — add it there rather than patching around it here.

- [ ] **Step 15: Commit**

```bash
git add -A
git commit -m "feat: select email transport by deployment mode and share invitation template"
```

---

### Task 5: Functional test host and the two-mode matrix

**Files:**
- Modify: `test/shared/FunctionalTestFactory.cs`
- Modify: `test/shared/SelfHostedTestFactory.cs` (renamed from `BillingDisabledTestFactory.cs` in Task 2)
- Modify: `test/functional/web/.../SelfHostedEndpointTests.cs` (renamed from `BillingDisabledEndpointTests.cs` in Task 2)
- Test: `test/functional/web/SaasModeTests.cs` (create)

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces: two functional hosts — the default SaaS one and `SelfHostedTestFactory`.

The base factory's mode flip and email keys landed in Task 2 Step 9, and the `IEmailService` substitution in Task 4 Step 13. All that remains here is the self-hosted factory's own email overrides and the matrix itself.

- [ ] **Step 1: Give the self-hosted factory its email overrides**

In `SelfHostedTestFactory.cs`, extend the in-memory override to:

```csharp
                ["Deployment:SelfHosted"] = "true",
                ["Email:Resend:ApiKey"] = string.Empty,
                ["Email:Smtp:Host"] = string.Empty
```

The empty SMTP host is the point: it selects `NoOpEmailService`, which is exactly what a self-hoster who has configured no relay gets. The in-memory collection is added last and therefore wins over the base factory's environment variables — the precedence the existing factory's own comment documents.

**Do not select the mode with an environment variable in any new factory.** Environment variables are process-global and TUnit runs tests in parallel, so two factories built concurrently would race on the value. `ConfigureAppConfiguration` + `AddInMemoryCollection` is per-host; follow that.

- [ ] **Step 2: Extend the self-hosted functional tests**

Add to the renamed `SelfHostedEndpointTests.cs` (which already asserts the billing endpoints 404 under `SelfHostedTestFactory`). Follow the file's existing conventions for host construction, authentication and tenant seeding. Add each of the following as its own `[Test]`, seeding a tenant whose subscription row is `SubscriptionTier.Free`:

0. `Ingest_ForDeactivatedTenant_IsStillBlocked` — deactivate the tenant, then attempt telemetry ingest; expect rejection. Self-hosted unlocks entitlements, **not** tenant deactivation: `IsIngestEligibleAsync` checks the tenant's active flag and nothing else, precisely so pending-deletion enforcement survives without depending on the subscription row. This test is what stops a future edit from making that member permissive along with its neighbours.
1. `AlertRuleCreate_OnFreeRow_Succeeds` — POST a custom alert rule; expect 200, not 403. This covers both the Team gate and the `CanCreateAlertRuleAsync` limit.
2. `IntegrationCreate_OnFreeRow_Succeeds` — POST a webhook integration; expect 200. Free's webhook limit is 0, so a delegated `CanCreateWebhookAsync` fails here.
3. `MachineAuthorizedKeyAdd_OnFreeRow_Succeeds` — expect 200.
4. `CommandSend_OnFreeRow_Succeeds` — expect 200.
5. `AuditLogList_OnFreeRow_Succeeds` — expect 200.
6. `Invitation_OnFreeRow_SucceedsAndHonoursRequestedRole` — invite a second member with role `Viewer`; expect 200 **and** assert the stored role is `Viewer`, not `TenantAdmin`. This is the deliberate behaviour change recorded in the spec.
7. `UpdateAdminSettings_InSelfHosted_Succeeds` — PUT `/api/v1/admin/settings` as a global admin; expect 200.
8. `BillingManagementEndpoints_InSelfHosted_Return404` — assert 404 for `POST /billing/cancel`, `POST /billing/downgrade`, `POST /billing/resume`, `POST /billing/reactivate`, `GET /billing/catalog`, `GET /billing/invoices`, `GET /billing/upcoming-invoice`.
9. `GetBillingSubscription_InSelfHosted_StillReturns200` — assert `GET /api/v1/billing/subscription` returns 200. It is unguarded and the web layout fetches it on every page load; a blanket 404 would break the application shell.
10. `EffectiveLimits_InSelfHosted_AreUnlimited` — assert the subscription payload reports `int.MaxValue` machine, alert-rule, webhook and member limits, and 365-day retention.

- [ ] **Step 3: Write the retention regression test**

Add to `test/functional/grpc/` (or `test/integration/` if telemetry ingest needs a real Postgres — check which project already exercises `TelemetryService`):

```
IngestedTelemetry_InSelfHosted_IsStampedLongRetention
```

Ingest one telemetry envelope for a tenant on a `Free` row in self-hosted mode, then assert the persisted row's `RetentionClass` is `Long`. This is the failure a user-interface test cannot catch: without it, the product looks unlocked while telemetry is deleted after one day.

- [ ] **Step 4: Write the SaaS regression tests**

Create `test/functional/web/SaasModeTests.cs` asserting the gates still bite in SaaS on a `Free` tenant:

1. `AlertRuleCreate_OnFreeRow_Returns403`
2. `IntegrationCreate_OnFreeRow_Returns403`
3. `AuditLogList_OnFreeRow_Returns403`
4. `Invitation_OnFreeRow_ForcesTenantAdminRole`
5. `UpdateAdminSettings_InSaas_Returns404`

- [ ] **Step 5: Run the functional suites**

```bash
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: all PASS. If a self-hosted test returns 403, a `SelfHostedSubscriptionService` member is still delegating — check it against the member table in Task 3.

- [ ] **Step 6: Add the runtime billing-isolation architecture test**

The existing `BillingContractBoundaryTests` in `test/unit/server/Architecture/` governs *compile-time* reachability of the control-plane contract by decoding assembly IL. It cannot see registration decisions. Add a sibling test asserting the *runtime* rule.

Create `test/unit/server/Architecture/SelfHostedBillingIsolationTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.Vord.BillingGrpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Architecture;

/// <summary>
/// A self-hosted deployment must not be able to reach the billing control plane at all. The
/// sibling BillingContractBoundaryTests enforces which code may reference the contract; this one
/// enforces that a self-hosted container never resolves a live client for it, so an accidental
/// registration cannot quietly reintroduce a Stripe dependency into the open-core product.
/// </summary>
public sealed class SelfHostedBillingIsolationTests
{
    private static ServiceCollection BuildServices(bool selfHosted)
    {
        ServiceCollection services = new();
        DeploymentMode mode = new(Options.Create(new DeploymentOptions { SelfHosted = selfHosted }));

        services.AddCoreServices(mode, new ObjectStorageOptions(), new EmailOptions(), new BillingOptions
        {
            GrpcUrl = "https://billing-api.invalid:12237",
        });

        return services;
    }

    [Test]
    public async Task SelfHosted_DoesNotRegisterBillingManagementClient()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        bool registered = services.Any(d => d.ServiceType == typeof(BillingManagement.BillingManagementClient));

        await Assert.That(registered).IsFalse();
    }

    [Test]
    public async Task SelfHosted_ResolvesNoOpBillingApiClient()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(IBillingApiClient));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(NoOpBillingApiClient));
    }

    /// <summary>
    /// The mirror assertion: without it, a registration that never fires in either mode would
    /// satisfy the tests above while silently breaking the hosted deployment.
    /// </summary>
    [Test]
    public async Task Saas_RegistersBillingManagementClient()
    {
        ServiceCollection services = BuildServices(selfHosted: false);

        bool registered = services.Any(d => d.ServiceType == typeof(BillingManagement.BillingManagementClient));

        await Assert.That(registered).IsTrue();
    }

    [Test]
    public async Task SelfHosted_DoesNotRegisterBillingWebhookHandler()
    {
        ServiceCollection services = BuildServices(selfHosted: true);

        bool registered = services.Any(d => d.ServiceType == typeof(IBillingWebhookHandler));

        await Assert.That(registered).IsFalse();
    }
}
```

If `unit.server` cannot reference `Framlux.Vord.BillingGrpc` without violating `BillingContractBoundaryTests`, add this test file's type to that test's permitted set and record the reason in the same commit — the permitted list is meant to be a deliberate, reviewable change.

- [ ] **Step 7: Run the architecture test**

```bash
dotnet run --project test/unit/server/unit.server.csproj --treenode-filter "*SelfHostedBillingIsolationTests*"
dotnet run --project test/unit/server/unit.server.csproj --treenode-filter "*BillingContractBoundaryTests*"
```

Expected: both PASS.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "test: cover both deployment modes across the entitlement surface"
```

---

### Task 6: Web surfaces the mode from the session

**Files:**
- Create: `src/services.core/Models/Users/DeploymentDto.cs`
- Modify: `src/services.core/Models/Users/UserDto.cs`
- Modify: `src/server/Endpoints/Web/Auth/AuthMeEndpoint.cs`
- Modify: `src/web/src/lib/api/types.ts`, `src/web/src/app.d.ts`, `src/web/src/lib/api/mock-client.ts`
- Modify: `src/web/src/routes/(admin)/admin/+page.server.ts`, `+page.svelte`
- Modify: `src/web/src/routes/(app)/settings/billing/+page.server.ts`
- Test: `test/functional/web/AuthMeDeploymentTests.cs`, plus Vitest coverage

**Interfaces:**
- Consumes: `DeploymentMode` (Task 1).
- Produces: `UserDto.Deployment` → `{ selfHosted: boolean }` on the `/auth/me` response.

- [ ] **Step 1: Create `DeploymentDto`**

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Users;

/// <summary>
/// Deployment facts the web application needs in order to render the right product. Carried on the
/// session payload the client already fetches, so the interface can never disagree with the server
/// about which mode is running.
/// </summary>
public sealed class DeploymentDto
{
    /// <summary>
    /// Whether the server is running as a self-hosted deployment. When true the client hides
    /// billing, tiers and upgrade prompts.
    /// </summary>
    public bool SelfHosted { get; set; }
}
```

- [ ] **Step 2: Add the property to `UserDto` and populate it**

In `src/services.core/Models/Users/UserDto.cs` add:

```csharp
    /// <summary>
    /// Deployment facts for the client. Not a property of the user; it travels on this payload
    /// because it is the response the web application already fetches and caches per session.
    /// </summary>
    public DeploymentDto Deployment { get; set; } = new();
```

In `AuthMeEndpoint.cs`, inject `DeploymentMode` and set the value **after** the `UserDto.FromPrincipal` call at line 58 (that method builds from cookie claims only, so setting it afterwards involves no claims round-trip):

```csharp
        dto.Deployment = new DeploymentDto { SelfHosted = _deploymentMode.IsSelfHosted };
```

- [ ] **Step 3: Write the failing functional test**

Create `test/functional/web/AuthMeDeploymentTests.cs` with two tests asserting `GET /api/v1/auth/me` returns `deployment.selfHosted` as `true` in a self-hosted host and `false` in a SaaS host.

- [ ] **Step 4: Run it to verify it fails, then passes**

```bash
dotnet run --project test/functional/web/functional.web.csproj --treenode-filter "*AuthMeDeploymentTests*"
```

- [ ] **Step 5: Mirror the type in the web app**

In `src/web/src/lib/api/types.ts` add to the `UserDto` interface:

```ts
	deployment?: { selfHosted: boolean };
```

It is optional because during a rolling deploy a new web container can briefly talk to an old api-server that does not send it.

Update `src/web/src/app.d.ts` if `PageData` needs the field. `mock-client.ts`'s `getMe()` returns an imported `mockUser` rather than an inline object, so the field is added in the fixture under `src/web/src/lib/api/mock-fixtures/` — follow the import to find it. Add `deployment: { selfHosted: false }`.

- [ ] **Step 6: Switch the admin page off the env var**

In `src/web/src/routes/(admin)/admin/+page.server.ts`:

```ts
	const selfHosted = locals.user?.deployment?.selfHosted === true;
```

Delete the `import { env } from '$env/dynamic/public';` line and the `billingEnabled` constant. Return `selfHosted` instead of `billingEnabled`. Add `locals` to the `load` destructuring if absent.

In `+page.svelte`, replace `const billingEnabled: boolean = $derived(data.billingEnabled);` with `const selfHosted: boolean = $derived(data.selfHosted);`, change line 133's `{billingEnabled ? '' : ', and settings'}` to `{selfHosted ? ', and settings' : ''}`, and line 343's `activeTab === 'settings' && billingEnabled === false` to `activeTab === 'settings' && selfHosted === true`.

The `=== true` comparison is deliberate: an `undefined` from an older server falls to the SaaS branch, which is correct because the mixed-version window only occurs on the SaaS cluster.

- [ ] **Step 7: Gate the billing route**

In `src/web/src/routes/(app)/settings/billing/+page.server.ts`, add to the top of `load`, after the existing role check:

```ts
	if (locals.user?.deployment?.selfHosted === true) {
		error(404, 'Not found');
	}
```

Change `billingEnabled: !!env.PUBLIC_BILLING_URL` in the returned object to `billingEnabled: !!env.PUBLIC_BILLING_URL` — **leave this unchanged**. `PUBLIC_BILLING_URL` remains a legitimate URL used by `createBillingClient`; it is no longer a mode signal, and the route-level guard above is what decides visibility.

- [ ] **Step 8: Hide tier-facing UI**

```bash
grep -rn "tier\|Tier\|upgrade\|Upgrade\|limit" src/web/src/routes src/web/src/lib/components --include="*.svelte" -l
```

For each component rendering a tier badge, an upgrade prompt or a usage-versus-limit meter, wrap it in `{#if selfHosted === false}`, threading `selfHosted` from `data.user.deployment?.selfHosted === true`.

- [ ] **Step 9: Run the web checks**

```bash
pnpm -C src/web check
pnpm -C src/web test
pnpm -C src/web build
```

Expected: zero TypeScript errors, zero warnings, all tests PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: derive web deployment mode from the session payload"
```

---

### Task 7: Self-hosted reference deployment and documentation

**Files:**
- Modify: `deployment/server/docker/docker-compose.yml`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update docker-compose**

In both the `api-server` and `services-worker` `environment` blocks, replace:

```yaml
      Billing__Enabled: "${Billing__Enabled:-false}"
      Resend__ApiKey: ${RESEND_API_KEY:-}
      Resend__FromEmail: ${RESEND_FROM_EMAIL:-}
```

with:

```yaml
      # This compose file is the reference self-hosted deployment. Leave this true — setting it
      # false requires a reachable billing API, which is not part of the self-hosted product.
      Deployment__SelfHosted: "true"
      # Email is optional. Leave SMTP_HOST empty and every send is skipped; set it to a relay
      # (Postfix, a provider's submission endpoint) and invitations and alerts are delivered.
      Email__FromEmail: ${EMAIL_FROM:-}
      Email__Smtp__Host: ${SMTP_HOST:-}
      Email__Smtp__Port: "${SMTP_PORT:-587}"
      Email__Smtp__Username: ${SMTP_USERNAME:-}
      Email__Smtp__Password: ${SMTP_PASSWORD:-}
      Email__Smtp__UseStartTls: "${SMTP_USE_STARTTLS:-true}"
```

- [ ] **Step 2: Verify compose still parses**

```bash
docker compose -f deployment/server/docker/docker-compose.yml config >/dev/null
```

Expected: exit 0. (`podman compose` is equivalent if Docker is unavailable.)

- [ ] **Step 3: Update CLAUDE.md**

Three edits:

1. Replace the whole **"Email (Resend) is optional"** paragraph. The new rule: the transport follows `Deployment:SelfHosted` — Resend in SaaS where a missing `Email:Resend:ApiKey` is a startup failure, SMTP in self-hosted where a missing `Email:Smtp:Host` selects `NoOpEmailService`. `EmailDeliveryOutcome`'s three states and the "`Skipped` is terminal success, never retry it" rule are unchanged. State plainly that an absent API key no longer implies self-hosted — the flag does.

2. In the **"Internal gRPC is mutual TLS on its own port"** paragraph, replace both "gated by `Billing:Enabled`" references with `Deployment:SelfHosted` being false, and note that `DeploymentOptionsValidator` refuses to start a self-hosted deployment with `InternalGrpc:Enabled` set.

3. Add a new **"Deployment mode"** paragraph under Architecture:

> **`Deployment:SelfHosted` is the single mode switch.** It defaults to `true` so a fresh clone runs with no configuration; the hosted deployment sets it to `false`. It alone decides the billing client (real versus `NoOpBillingApiClient`), whether `BillingGatewayService` and `FleetAdminService` are mapped, whether `StripeSyncJob` is registered, the email transport, and whether entitlement limits apply. `Billing:Enabled` was deleted — a second switch is exactly the drift this replaced. In self-hosted, `SelfHostedSubscriptionService` decorates `ISubscriptionService` and answers every entitlement question permissively; it must implement **all eleven** interface members, because a delegated member silently reimposes a Free-tier limit that the interface does not reflect. Retention is the member most easily missed: it does not flow through `EffectiveLimits`, and it is capped at `RetentionClassPolicy.LongWindowDays`, not unlimited, because there is no unlimited retention class.

- [ ] **Step 4: Full verification**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
pnpm -C src/web check && pnpm -C src/web test && pnpm -C src/web build
```

Expected: everything green, zero warnings. Do not proceed to Task 8 otherwise.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs: document deployment mode and switch compose to SMTP email"
```

---

### Task 8: `framlux/stack` rollout

**Repository:** `/Users/jonathanmiller/Repositories/framlux/stack` — separate repo, separate commits. ArgoCD auto-syncs `main`, so **each phase is its own commit and each must be observed healthy before the next**.

**Files:**
- Modify: `clusters/prod/apps/vord-platform/base/kustomization.yaml`
- Modify: `clusters/prod/apps/vord-platform/base/fleet/{api-server,services-worker,web,migration-runner}/deployment.yaml`

Read the spec's "SaaS deployment" section first. The ordering is the safety mechanism, not a convenience.

- [ ] **Step 1 (Phase 1): Add the inert keys**

In the `fleet-config` `configMapGenerator` literals, add after `Auth__CookieDomain`:

```yaml
      # The single switch between the hosted and self-hosted products. This literal is
      # load-bearing: the application defaults to self-hosted, so removing this line silently
      # disables billing and the internal control plane on this cluster.
      - Deployment__SelfHosted=false
```

And alongside the existing `Resend__FromEmail` line:

```yaml
      - Email__FromEmail=Framlux Vord <invitations@outreach.framlux.io>
```

Leave `Billing__Enabled=true` and `Resend__FromEmail` in place for now.

Reseal `vord-secret` (`clusters/prod/apps/vord-platform/base/shared/sealed-secret.yaml`, which carries `Resend__ApiKey` at line 27) to add `Email__Resend__ApiKey` with the same value.

**This step is not agent-executable and must be done by the operator.** It needs the plaintext Resend API key, which exists only outside the repository, and a scope-correct seal:

```bash
kubeseal --name vord-secret --namespace vord-fleet --format yaml
```

The name and namespace must match exactly — SealedSecrets are scoped by both, and a mismatch produces a secret the controller silently refuses to unseal. See `stack/docs/runbooks/` for the established procedure.

- [ ] **Step 2: Verify the render, then commit phase 1**

```bash
kustomize build clusters/prod >/dev/null
```

Expected: exit 0.

```bash
git add clusters/prod/apps/vord-platform/base/kustomization.yaml \
        clusters/prod/apps/vord-platform/base/shared/sealed-secret.yaml
git commit -m "config: stage deployment mode and email keys ahead of the fleet upgrade"
```

- [ ] **Step 3: Observe phase 1 land**

The `fleet-config` content hash changes, so all four fleet workloads roll. Every added key is unbound in the running 3.2.0 build and .NET configuration ignores unbound keys, so behaviour must be identical.

```bash
kubectl -n vord-fleet rollout status deploy/api-server
kubectl -n vord-fleet rollout status deploy/services-worker
kubectl -n vord-fleet rollout status deploy/web
```

Expected: all three complete; pods `Running` and `Ready`. Confirm invitations and billing still work before continuing. **If anything is unhealthy, stop — the assumption that these keys are inert is wrong and the rest of the plan depends on it.**

- [ ] **Step 4 (Phase 2): Cut the vord release**

In the vord repository, on a green `main`:

```bash
git switch main && git pull
git tag server-v3.3.0
git push origin server-v3.3.0
```

Wait for `.github/workflows/prod.yaml` to publish `api-server`, `services-worker`, `web` and `migration_runner` at `3.3.0`.

- [ ] **Step 5 (Phase 3): Bump the image tags**

Change `3.2.0` to `3.3.0` in all four files:

- `clusters/prod/apps/vord-platform/base/fleet/api-server/deployment.yaml` (line ~44)
- `clusters/prod/apps/vord-platform/base/fleet/services-worker/deployment.yaml` (line ~42)
- `clusters/prod/apps/vord-platform/base/fleet/web/deployment.yaml` (line ~38)
- `clusters/prod/apps/vord-platform/base/fleet/migration-runner/deployment.yaml`

```bash
kustomize build clusters/prod >/dev/null
git add clusters/prod/apps/vord-platform/base/fleet
git commit -m "release: roll the fleet to 3.3.0"
```

- [ ] **Step 6: Observe phase 3 and verify SaaS behaviour**

```bash
kubectl -n vord-fleet rollout status deploy/api-server
kubectl -n vord-fleet logs -n vord-fleet deploy/api-server | grep -i "deployment\|self.hosted\|billing"
```

Confirm: pods healthy; the billing page renders; the admin settings tab is absent; a test invitation is delivered. A startup failure mentioning `Email:Resend:ApiKey` means phase 1's reseal did not land — roll the tags back to 3.2.0.

- [ ] **Step 7 (Phase 4): Clean up**

Remove **only** these two literals from `fleet-config`:

```yaml
      - Billing__Enabled=true
      - Resend__FromEmail=Framlux Vord <invitations@outreach.framlux.io>
```

**Do not remove `Resend__ApiKey` from `vord-secret`.** `vord-secret` is shared: `billing/api/deployment.yaml:68` mounts it and vord-internal's billing-api binds `Resend:ApiKey` directly, with a Reloader annotation on line 10. Deleting the key would restart billing-api with its Resend sender silently swapped out. The two keys coexist — `Resend__ApiKey` is billing-api's, `Email__Resend__ApiKey` is the fleet's.

While in the file, correct the stale comment claiming a `vord-secret` change "triggers no rollout of its own": Reloader watches it on `api-server`, `services-worker` and `billing-api`, so it does.

```bash
kustomize build clusters/prod >/dev/null
git add clusters/prod/apps/vord-platform/base/kustomization.yaml
git commit -m "config: drop the superseded billing and resend fleet literals"
kubectl -n vord-fleet rollout status deploy/api-server
```

- [ ] **Step 8: Update the user memory**

Replace `~/.claude/projects/-Users-jonathanmiller-Repositories-framlux/memory/vord-self-hosted-mode-signal.md`. The recorded inference — absent `Resend:ApiKey` means self-hosted — is no longer true. The new fact: `Deployment:SelfHosted` is the single mode switch, defaulting to `true`; in SaaS a missing `Email:Resend:ApiKey` is a hard startup failure. Update the `MEMORY.md` pointer line to match.

---

## Verification Checklist

- [ ] `grep -rn "Billing:Enabled\|Billing__Enabled\|BillingStatus\|billingEnabled" src/ test/ deployment/` returns nothing in vord.
- [ ] `grep -rn "ResendOptions\|Resend__ApiKey\|Resend:ApiKey" src/ test/` returns nothing in vord.
- [ ] `SelfHostedSubscriptionService` implements all eleven `ISubscriptionService` members (Task 3 Step 6), sets all five `required` members on the synthetic subscription, and bases `IsIngestEligibleAsync` on the tenant active flag alone.
- [ ] A functional test proves a deactivated tenant still cannot ingest in self-hosted mode.
- [ ] `grep -rn "BillingDisabled" test/` returns nothing.
- [ ] Both functional mode suites pass.
- [ ] The retention regression test asserts `RetentionClass.Long`.
- [ ] `pnpm -C src/web check` reports zero errors and zero warnings.
- [ ] `kustomize build clusters/prod` renders after every stack phase.
- [ ] `Resend__ApiKey` still present in `vord-secret`.
