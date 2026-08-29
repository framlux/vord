# Email Delivery Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close `vord-xoo` by removing the remaining silent-email-failure paths in `vord-internal`, proving invitation delivery end to end, and pushing the fixes already committed locally in `vord`.

**Architecture:** `vord` already has the fix (startup validation plus Error-level logging of Resend rejections, commits `57a92a3` and `d012de0`, unpushed). This plan ports the same guarantees to `vord-internal`'s independent `ResendOptions` / `ResendEmailSender`, which were never touched, then pushes and verifies.

**Tech Stack:** .NET 10, TUnit (run via `dotnet run`, never `dotnet test`), FastEndpoints, `Microsoft.Extensions.Options` validation, Resend HTTP API.

## Global Constraints

- Every code file starts with the three-line Framlux license header, copied verbatim from any neighbouring file.
- No `var` — explicit types only (error-level in `.editorconfig`).
- File-scoped namespaces; Allman braces; private fields `_camelCase`; no `this.` qualifier.
- XML doc comments on all public members (CS1591 is a warning and the build must be warning-free).
- No Yoda conditions. Never `!boolean` — write `(x == false)`. Parenthesise compound conditions.
- Logical operators stay on the same line as their operands.
- `using` directives in alphabetical order.
- Blank line before every `return` except when the preceding line is a comment. Blank line at end of file.
- One type per file.
- Tests run with `dotnet run --project <path>`, not `dotnet test`.
- **TUnit filter syntax:** `--treenode-filter` takes a path form, `/Assembly/Namespace/Class/Method`, so a class filter is `--treenode-filter "/*/*/ResendOptionsValidatorTests/*"`. The `"*Name*"` form written in the steps below matches **nothing** and still exits zero. Treat a filtered run that reports zero tests as a failure and correct the filter — never as a pass.
- Commit messages: no AI attribution, no `Co-Authored-By`, no session footer.
- No review IDs, task numbers, or phase labels in code or comments.

## File Structure

**`vord-internal` (all new/modified code lives here):**
- Create `src/billing-api/Configuration/ResendOptionsValidator.cs` — startup validation, one responsibility.
- Modify `src/billing-api/Program.cs:103-104` — register the validator, add `ValidateOnStart()`.
- Modify `src/billing-api/Services/ResendEmailSender.cs:42-45` — surface the Resend rejection body before throwing.
- Modify `src/billing-api/Services/StripeCanaryService.cs:165-189` — stop an email failure from destroying the canary's health record.
- Create `test/billing/Configuration/ResendOptionsValidatorTests.cs`.
- Modify `test/billing/Services/ResendEmailSenderTests.cs`, `test/billing/Configuration/StripeCanaryOptionsValidatorTests.cs` — fixture addresses.

**`vord`:**
- Modify `test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs:26` — fixture address.

---

### Task 1: Fail startup on a Resend API key with no sender

`src/billing-api/Program.cs:103` binds `ResendOptions` with neither a validator nor `ValidateOnStart()`, while `InternalApiOptions` (line 98) and `StripeCanaryOptions` (line 106) on either side of it both validate. `FromEmail` defaults to `string.Empty`, so a key-without-sender deployment starts cleanly and every send is rejected by Resend.

**Files:**
- Create: `vord-internal/src/billing-api/Configuration/ResendOptionsValidator.cs`
- Modify: `vord-internal/src/billing-api/Program.cs:103-104`
- Test: `vord-internal/test/billing/Configuration/ResendOptionsValidatorTests.cs`

**Interfaces:**
- Consumes: `ResendOptions` (`src/billing-api/Configuration/ResendOptions.cs`) with `string ApiKey` and `string FromEmail`, both defaulting to `string.Empty`.
- Produces: `public sealed class ResendOptionsValidator : IValidateOptions<ResendOptions>` in namespace `Framlux.Billing.Api.Configuration`, with `ValidateOptionsResult Validate(string? name, ResendOptions options)`.

- [ ] **Step 1: Write the failing tests**

Create `vord-internal/test/billing/Configuration/ResendOptionsValidatorTests.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Billing.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Framlux.Billing.Api.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ResendOptionsValidator"/>. The rule is conditional: email is optional,
/// so a deployment with no API key is valid and simply sends nothing. An API key with no sender
/// address is not valid — Resend rejects every such send and the only trace is a log line, so it
/// has to fail at startup instead.
/// </summary>
public sealed class ResendOptionsValidatorTests
{
    /// <summary>
    /// Email switched off entirely is a supported deployment; the no-op sender is used.
    /// </summary>
    [Test]
    public async Task Validate_NoApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(null, new ResendOptions());

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A sender address with no API key is harmless: nothing is sent either way.
    /// </summary>
    [Test]
    public async Task Validate_FromEmailWithoutApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { FromEmail = "Framlux Vord Billing <alerts@outreach.framlux.io>" });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// Both configured is the normal production case.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyAndFromEmail_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions
            {
                ApiKey = "re_test_key",
                FromEmail = "Framlux Vord Billing <alerts@outreach.framlux.io>",
            });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// The regression this validator exists for: billing-api shipped with an API key and no
    /// sender, so the Stripe canary's alert emails were sent with an empty From and rejected.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyWithoutFromEmail_Fails()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "re_test_key" });

        await Assert.That(result.Failed).IsTrue();
        await Assert.That(result.FailureMessage).Contains("Resend:FromEmail is required");
    }

    /// <summary>
    /// Whitespace is not a sender address.
    /// </summary>
    [Test]
    public async Task Validate_ApiKeyWithWhitespaceFromEmail_Fails()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "re_test_key", FromEmail = "   " });

        await Assert.That(result.Failed).IsTrue();
    }

    /// <summary>
    /// A whitespace-only API key counts as unconfigured, so no sender is required.
    /// </summary>
    [Test]
    public async Task Validate_WhitespaceApiKey_Succeeds()
    {
        ResendOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            null,
            new ResendOptions { ApiKey = "   " });

        await Assert.That(result.Succeeded).IsTrue();
    }

    /// <summary>
    /// A null options instance is a programming error and must not be silently accepted.
    /// </summary>
    [Test]
    public async Task Validate_NullOptions_ThrowsArgumentNullException()
    {
        ResendOptionsValidator validator = new();

        await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*ResendOptionsValidatorTests*"
```

Expected: compile failure — `ResendOptionsValidator` does not exist.

- [ ] **Step 3: Write the validator**

Create `vord-internal/src/billing-api/Configuration/ResendOptionsValidator.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;

namespace Framlux.Billing.Api.Configuration;

/// <summary>
/// Validates <see cref="ResendOptions"/> configuration.
/// Email is optional: with no API key the no-op sender is registered and nothing is sent, which
/// is a supported deployment. But a configured API key with no sender address is not a working
/// configuration — Resend rejects any send whose From address is missing or on an unverified
/// domain, and the rejection is only visible in logs, so the failure mode is alert mail that
/// silently never arrives. Failing at startup turns that into an obvious misconfiguration.
/// </summary>
public sealed class ResendOptionsValidator : IValidateOptions<ResendOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ResendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.FromEmail))
        {
            return ValidateOptionsResult.Fail(
                "Resend:FromEmail is required when Resend:ApiKey is configured. It must be an address on a domain verified in Resend, or every send is rejected.");
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*ResendOptionsValidatorTests*"
```

Expected: 7 tests pass.

- [ ] **Step 5: Register the validator**

In `vord-internal/src/billing-api/Program.cs`, replace lines 103-104:

```csharp
builder.Services.AddOptions<ResendOptions>()
    .BindConfiguration("Resend");
```

with:

```csharp
builder.Services.AddOptions<ResendOptions>()
    .BindConfiguration("Resend")
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ResendOptions>, ResendOptionsValidator>();
```

Confirm `using Microsoft.Extensions.Options;` is present in the file's using block, in alphabetical order. `StripeCanaryOptionsValidator` is registered the same way three lines below, so the pattern already exists.

- [ ] **Step 6: Build and run the full billing test suite**

```bash
dotnet build vord-internal.slnx -c Release
dotnet run --project test/billing/billing.csproj
```

Expected: build succeeds with zero warnings; all tests pass. `ValidateOnStart` runs at host build time, so a functional test factory that configures a Resend API key without a sender would now fail — if any test does that, fix the test's configuration rather than weakening the validator.

- [ ] **Step 7: Commit**

```bash
git add src/billing-api/Configuration/ResendOptionsValidator.cs src/billing-api/Program.cs test/billing/Configuration/ResendOptionsValidatorTests.cs
git commit -m "Fail billing-api startup on a Resend key with no sender"
```

---

### Task 2: Keep the Resend rejection reason instead of discarding it

`ResendEmailSender.SendAsync` calls `response.EnsureSuccessStatusCode()` (line 43), which throws an `HttpRequestException` carrying only the status code. The Resend response body is where `"The vordfleet.dev domain is not verified"` actually lives, and it is thrown away. `vord`'s equivalent service already logs status and body; `vord-internal` does not.

**Files:**
- Modify: `vord-internal/src/billing-api/Services/ResendEmailSender.cs:42-45`
- Test: `vord-internal/test/billing/Services/ResendEmailSenderTests.cs`

**Interfaces:**
- Consumes: `IEmailSender.SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)` returning `Task`.
- Produces: unchanged signature and unchanged throwing behaviour — only the log output changes. Callers are unaffected.

- [ ] **Step 1: Write the failing test**

Append to `vord-internal/test/billing/Services/ResendEmailSenderTests.cs`. Match the existing file's fake-handler helper; if it constructs `HttpClient` from a stub `HttpMessageHandler`, reuse that helper rather than adding a second one.

```csharp
    /// <summary>
    /// A rejected send must log the Resend status code and response body at Error. The body is
    /// where the actual reason lives ("domain is not verified"), and losing it is what made the
    /// invitation outage invisible for months.
    /// </summary>
    [Test]
    public async Task SendAsync_RejectedByResend_LogsStatusAndBodyAtError()
    {
        FakeLogger<ResendEmailSender> logger = new();
        HttpClient client = CreateClient(
            HttpStatusCode.Forbidden,
            "{\"message\":\"The vordfleet.dev domain is not verified\"}");
        ResendEmailSender sender = new(
            client,
            Options.Create(new ResendOptions
            {
                ApiKey = "re_test",
                FromEmail = "alerts@outreach.framlux.io",
            }),
            logger);

        await Assert.That(async () => await sender.SendAsync("to@example.com", "s", "<p>b</p>", CancellationToken.None))
            .Throws<HttpRequestException>();

        FakeLogRecord record = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Error);
        await Assert.That(record.Message).Contains("403");
        await Assert.That(record.Message).Contains("domain is not verified");
    }
```

Add to the file's using block, keeping alphabetical order: `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Testing`, `Microsoft.Extensions.Options`, `System.Linq`, `System.Net`.

If `Microsoft.Extensions.Diagnostics.Testing` (which provides `FakeLogger<T>`) is not already referenced by `test/billing/billing.csproj`, add it:

```bash
dotnet add test/billing/billing.csproj package Microsoft.Extensions.Diagnostics.Testing
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*SendAsync_RejectedByResend*"
```

Expected: FAIL — no Error-level record is emitted, so `.Single(...)` throws.

- [ ] **Step 3: Log the body before throwing**

In `vord-internal/src/billing-api/Services/ResendEmailSender.cs`, replace lines 42-45:

```csharp
        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Sent email to {ToEmail} via Resend", toEmail);
```

with:

```csharp
        using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);

        if (response.IsSuccessStatusCode == false)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Resend rejected an email to {ToEmail}: {StatusCode} {Body}",
                toEmail,
                (int)response.StatusCode,
                body);
        }

        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Sent email to {ToEmail} via Resend", toEmail);
```

Throwing behaviour is deliberately unchanged so callers keep their current semantics.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*ResendEmailSenderTests*"
```

Expected: all tests in the class pass, including the pre-existing ones.

- [ ] **Step 5: Commit**

```bash
git add src/billing-api/Services/ResendEmailSender.cs test/billing/Services/ResendEmailSenderTests.cs test/billing/billing.csproj
git commit -m "Log the Resend status and body when a send is rejected"
```

---

### Task 3: Stop a failed alert email from destroying the canary health record

Verified while writing this plan, and beyond the original bead text. In `StripeCanaryService.RunTickAsync`, the alert and recovery sends at lines 173 and 183 are not wrapped. `SendAsync` throws on any non-2xx, the exception escapes `RunTickAsync`, and the broad `catch (Exception)` in `ExecuteAsync` (line 93) logs it and moves on. Everything after the send is skipped — including the `healthStore.UpsertAsync` that records the tick.

So a rejected alert email means the canary's health row silently goes stale, and the admin panel reports old data while the operator believes the canary is running. This is the same failure class as `vord-xoo`: an email failure quietly damaging something else.

Delivering the alert is best-effort; recording the tick is not.

**Files:**
- Modify: `vord-internal/src/billing-api/Services/StripeCanaryService.cs:165-189`
- Test: `vord-internal/test/billing/Services/StripeCanaryServiceTests.cs`

**Interfaces:**
- Consumes: `IEmailSender.SendAsync(...)`, `ICanaryHealthStore.UpsertAsync(StripeCanaryHealth health, CancellationToken ct)`, `StripeCanaryService.RunTickAsync(CanaryConfig cfg, CancellationToken ct)`.
- Produces: `RunTickAsync` no longer propagates exceptions originating from the email send. All other behaviour is unchanged.

- [ ] **Step 1: Write the failing test**

Append to `vord-internal/test/billing/Services/StripeCanaryServiceTests.cs`, following the existing NSubstitute setup in that file for building a `StripeCanaryService` with substituted probe, health store, and scope factory:

```csharp
    /// <summary>
    /// A rejected alert email must not abort the tick. Delivery is best-effort; recording the
    /// tick is not, and losing the health row makes the admin panel report stale canary state.
    /// </summary>
    [Test]
    public async Task RunTickAsync_AlertEmailThrows_StillRecordsHealth()
    {
        IEmailSender emailSender = Substitute.For<IEmailSender>();
        emailSender
            .SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("Resend rejected the send"));

        ICanaryHealthStore healthStore = Substitute.For<ICanaryHealthStore>();
        StripeCanaryService service = CreateFailingCanaryService(emailSender, healthStore);

        await service.RunTickAsync(CreateAlertingConfig(), CancellationToken.None);

        await healthStore
            .Received(1)
            .UpsertAsync(Arg.Any<StripeCanaryHealth>(), Arg.Any<CancellationToken>());
    }
```

Build `CreateFailingCanaryService` and `CreateAlertingConfig` as private helpers in the test class, wiring a probe that reports failure and a `StripeCanaryOptions` whose `ConsecutiveFailuresToAlert` threshold is met, so `shouldRaiseAlert` evaluates true. Reuse whatever scope-factory substitute the existing tests in this file already use.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*RunTickAsync_AlertEmailThrows*"
```

Expected: FAIL — `HttpRequestException` propagates out of `RunTickAsync` and `UpsertAsync` is never reached.

- [ ] **Step 3: Make delivery best-effort**

In `vord-internal/src/billing-api/Services/StripeCanaryService.cs`, wrap both sends. Replace the `if (shouldRaiseAlert) { ... } else if (shouldSendRecovery) { ... }` block at lines 165-189 with:

```csharp
        if (shouldRaiseAlert)
        {
            _logger.LogCritical(
                "ALERT: Stripe sandbox canary failing. Leg: {LastErrorLeg}, error: {LastErrorCode}, consecutive failures: {ConsecutiveFailures}",
                result.LastErrorLeg,
                result.LastErrorCode,
                consecutiveFailures);
            await TrySendAsync(
                scope,
                options.AlertToEmail,
                "Stripe sandbox canary is failing",
                BuildFailureEmailBody(result, consecutiveFailures),
                ct);
            alertingState = true;
        }
        else if (shouldSendRecovery)
        {
            await TrySendAsync(
                scope,
                options.AlertToEmail,
                "Stripe sandbox canary has recovered",
                BuildRecoveryEmailBody(),
                ct);
            alertingState = false;
        }
```

Add this private method to the class, placed after `RunTickAsync`:

```csharp
    /// <summary>
    /// Sends a canary notification without letting a delivery failure abort the tick. Recording
    /// the health row matters more than the email: if a send failure escaped, the tick would be
    /// abandoned and the admin panel would keep reporting stale canary state.
    /// </summary>
    private async Task TrySendAsync(
        IServiceScope scope,
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct)
    {
        try
        {
            IEmailSender emailSender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            await emailSender.SendAsync(toEmail, subject, htmlBody, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send the Stripe canary notification to {ToEmail}", toEmail);
        }
    }
```

The state change stays outside the `try` — `alertingState` must flip regardless, or a persistently broken mailbox would re-raise the same alert on every tick forever.

Note the deliberate exception here: the broad `catch (Exception)` is correct in this one place because the alternative is losing the health record, and the exception is logged at Error where the Task 4 alert rule will see it.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet run --project test/billing/billing.csproj --treenode-filter "*StripeCanaryServiceTests*"
```

Expected: the new test passes and every pre-existing canary test still passes.

- [ ] **Step 5: Cancellation must still cancel**

Confirm `RunTickAsync` still honours cancellation: `OperationCanceledException` raised by a cancelled `ct` inside `TrySendAsync` is now swallowed and logged rather than propagating. Verify `ExecuteAsync`'s `while (stoppingToken.IsCancellationRequested == false)` still exits promptly, because the `Task.Delay` on line 98 continues to observe the token. Add a test asserting a cancelled token ends the loop without an unhandled exception:

```csharp
    /// <summary>
    /// A cancelled token must stop the tick without attempting delivery, even though the email
    /// helper now swallows exceptions including cancellation. Asserting on the send is what makes
    /// this a real test rather than a smoke check: it proves cancellation short-circuits the work
    /// rather than merely failing quietly inside the new catch block.
    /// </summary>
    [Test]
    public async Task RunTickAsync_CancelledToken_DoesNotAttemptDelivery()
    {
        IEmailSender emailSender = Substitute.For<IEmailSender>();
        ICanaryHealthStore healthStore = Substitute.For<ICanaryHealthStore>();
        StripeCanaryService service = CreateFailingCanaryService(emailSender, healthStore);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await service.RunTickAsync(CreateAlertingConfig(), cts.Token);

        await emailSender
            .DidNotReceive()
            .SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
```

Run: `dotnet run --project test/billing/billing.csproj --treenode-filter "*StripeCanaryServiceTests*"` — expected PASS.

- [ ] **Step 6: Commit**

```bash
git add src/billing-api/Services/StripeCanaryService.cs test/billing/Services/StripeCanaryServiceTests.cs
git commit -m "Record canary health even when the notification email fails"
```

---

### Task 4: Align stale test fixtures onto the verified domain

Four fixtures still use `@vordfleet.dev` senders. They pass, but they are the wrong example for anyone copying them, and `vordfleet.dev` is precisely the unverified domain that caused the outage.

**Files:**
- Modify: `vord-internal/test/billing/Services/ResendEmailSenderTests.cs:29,44`
- Modify: `vord-internal/test/billing/Configuration/StripeCanaryOptionsValidatorTests.cs:43,64,84,105,147`
- Modify: `vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs:26`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing new. Test data only.

- [ ] **Step 1: Update the vord-internal sender fixtures**

In `test/billing/Services/ResendEmailSenderTests.cs`, change `FromEmail = "alerts@vordfleet.dev"` to `FromEmail = "alerts@outreach.framlux.io"` (line 29) and update the matching assertion on line 44 to expect `"alerts@outreach.framlux.io"`.

In `test/billing/Configuration/StripeCanaryOptionsValidatorTests.cs`, change each `AlertToEmail = "alerts@vordfleet.dev"` to `AlertToEmail = "alerts@outreach.framlux.io"` (lines 43, 64, 84, 105, 147). `AlertToEmail` is a recipient rather than a sender, so this is consistency only, not a correctness fix.

- [ ] **Step 2: Run the vord-internal suite**

```bash
dotnet run --project test/billing/billing.csproj
```

Expected: all tests pass.

- [ ] **Step 3: Update the vord fixture**

In `vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs:26`, change `FromEmail = "Test <test@vordfleet.dev>"` to `FromEmail = "Test <test@outreach.framlux.io>"`.

- [ ] **Step 4: Run the vord unit suite**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj
```

Expected: all tests pass. If a stale TUnit result is suspected, rebuild with `--no-incremental` and run the compiled executable directly rather than trusting `dotnet run --no-build`.

- [ ] **Step 5: Commit in both repos**

```bash
git -C vord-internal add test/billing/Services/ResendEmailSenderTests.cs test/billing/Configuration/StripeCanaryOptionsValidatorTests.cs
git -C vord-internal commit -m "Use the verified sender domain in email test fixtures"

git -C vord add test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs
git -C vord commit -m "Use the verified sender domain in email test fixtures"
```

---

### Task 4b: Fail gracefully when Resend is not configured

`Resend:ApiKey` is optional — a self-hosted deployment without one must start cleanly, send nothing, and generate no failures. Today the fleet side does the opposite.

`ResendEmailService` returns `false` for an unconfigured API key after logging a warning (`ResendEmailService.cs:39-43` and `:108-112`). Both callers treat `false` as a transient send failure:

- `SendInvitationEmailJob.SendAsync` throws `InvalidOperationException` (`SendInvitationEmailJob.cs:46-50`) so Hangfire's `[AutomaticRetry(Attempts = 3)]` retries, then the job lands permanently in the Failed state.
- `AlertDeliveryService` releases the claim for retry and records a transient failure (`AlertDeliveryService.cs:118-126`).

So on a self-hosted install with no key, every invitation and every alert email burns its retry budget and accumulates permanently failed Hangfire jobs. The observability plan then alerts `VordHangfireJobsFailing` at `> 10` failed jobs — critical severity, which pages. A supported configuration would page the operator forever.

**This reverses a deliberate, documented decision.** `AlertDeliveryService.cs:120-123` states the current behaviour is intentional: *"SendAlertEmailAsync returns false for both transient transport/5xx failures and the intentional no-API-key no-op. Treat false as transient so Hangfire retries... A permanently-misconfigured key drains the retry budget and surfaces in the Failed tab, which is the correct operator signal."* That reasoning is sound for a **misconfigured** key. It is wrong for a **deliberately absent** one, and the overloaded `bool` is what makes the two indistinguishable.

The fix is to stop overloading the return value.

**Files:**
- Create: `vord/src/services.core/Services/Notifications/EmailDeliveryOutcome.cs`
- Modify: `vord/src/services.core/Services/Notifications/IEmailService.cs:21,31`
- Modify: `vord/src/services.core/Services/Notifications/ResendEmailService.cs:34,39-43,86,90-92,98,103,108-112,130,134-136,142`
- Modify: `vord/src/services.core/Services/Notifications/SendInvitationEmailJob.cs:44-50`
- Modify: `vord/src/services.core/Services/Alerts/AlertDeliveryService.cs:113-126`
- Test: `vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs`, `SendInvitationEmailJobTests.cs`, and the alert-delivery tests

**Interfaces:**
- Consumes: `IEmailService.SendInvitationEmailAsync(...)`, `IEmailService.SendAlertEmailAsync(...)`, currently both `Task<bool>`.
- Produces: `public enum EmailDeliveryOutcome { Sent, Skipped, Failed }`; both interface methods return `Task<EmailDeliveryOutcome>`.

- [ ] **Step 1: Write the failing tests**

In `SendInvitationEmailJobTests.cs`:

```csharp
    /// <summary>
    /// Email is optional. With no provider configured the job must complete quietly rather than
    /// throwing — otherwise every invitation on a self-hosted install burns its Hangfire retry
    /// budget and accumulates permanently failed jobs.
    /// </summary>
    [Test]
    public async Task SendAsync_EmailSkippedBecauseUnconfigured_DoesNotThrow()
    {
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService
            .SendInvitationEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EmailDeliveryOutcome.Skipped);
        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        await job.SendAsync("to@example.com", "Tenant", "Inviter", "https://example.com/accept", CancellationToken.None);
    }

    /// <summary>
    /// A real delivery failure must still throw so Hangfire retries — the graceful-skip path must
    /// not swallow genuine failures.
    /// </summary>
    [Test]
    public async Task SendAsync_EmailFailed_ThrowsSoHangfireRetries()
    {
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService
            .SendInvitationEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(EmailDeliveryOutcome.Failed);
        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        await Assert.That(async () => await job.SendAsync("to@example.com", "Tenant", "Inviter", "https://example.com/accept", CancellationToken.None))
            .Throws<InvalidOperationException>();
    }
```

Adapt the constructor call to `SendInvitationEmailJob`'s actual signature — read the existing tests in that file first and follow their construction pattern.

In `ResendEmailServiceTests.cs`, add tests asserting `SendInvitationEmailAsync` and `SendAlertEmailAsync` return `EmailDeliveryOutcome.Skipped` when `ApiKey` is empty **and** when it is whitespace-only, and `EmailDeliveryOutcome.Failed` on a non-2xx response.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "/*/*/SendInvitationEmailJobTests/*"
```

Expected: compile failure — `EmailDeliveryOutcome` does not exist.

- [ ] **Step 3: Add the outcome type**

Create `vord/src/services.core/Services/Notifications/EmailDeliveryOutcome.cs`:

```csharp
// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// The result of attempting to deliver an email. A single boolean could not distinguish "no
/// provider is configured, which is a supported deployment" from "the provider rejected this
/// message", so callers treated both as retryable and a keyless install accumulated permanently
/// failed jobs.
/// </summary>
public enum EmailDeliveryOutcome
{
    /// <summary>
    /// The message was accepted by the provider.
    /// </summary>
    Sent,

    /// <summary>
    /// No email provider is configured, so nothing was sent. Not a failure — retrying cannot help
    /// and the caller must treat this as success.
    /// </summary>
    Skipped,

    /// <summary>
    /// The provider was configured but rejected the message or was unreachable. Retrying may help.
    /// </summary>
    Failed,
}
```

- [ ] **Step 4: Change the interface and implementation**

In `IEmailService.cs`, change both methods to return `Task<EmailDeliveryOutcome>`, updating their XML docs to describe the three outcomes.

In `ResendEmailService.cs`: return `EmailDeliveryOutcome.Skipped` from the two unconfigured branches, `Sent` where it returned `true`, and `Failed` from every rejection and exception branch. Change both API-key checks from `string.IsNullOrEmpty` to `string.IsNullOrWhiteSpace`, so a whitespace-only key counts as unconfigured — this matches `ResendOptionsValidator`, which already treats a whitespace key as absent, and prevents the service attempting a send with a blank sender.

Keep the existing warning log on the skip path, but make sure it reads as an expected condition rather than a fault.

- [ ] **Step 5: Update the callers**

`SendInvitationEmailJob.SendAsync` — throw only on `Failed`:

```csharp
        EmailDeliveryOutcome outcome = await _emailService.SendInvitationEmailAsync(toEmail, tenantName, inviterName, acceptUrl, ct);

        if (outcome == EmailDeliveryOutcome.Failed)
        {
            throw new InvalidOperationException($"Failed to send invitation email to {toEmail} for tenant '{tenantName}'. Hangfire will retry.");
        }
```

Update the class's XML doc, which currently states the job throws when the send "returns false".

`AlertDeliveryService` — treat `Skipped` as terminal success rather than a transient failure, so the claim is not released for retry and no transient failure is recorded. Replace the `else` branch's blanket handling with an explicit `Failed` check, and update the `:120-123` comment to describe the new three-state contract.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
dotnet run --project test/functional/web/functional.web.csproj
```

Expected: all pass. Every existing test asserting the old `bool` contract must be updated to the enum — if any test still compiles against `bool`, the interface change was incomplete.

- [ ] **Step 7: Commit**

```bash
git add src/services.core/Services/Notifications src/services.core/Services/Alerts/AlertDeliveryService.cs test/unit/services.core test/functional
git commit -m "Distinguish a skipped email from a failed one so a keyless install stays healthy"
```

---

### Task 4c: One rule for email log levels

The owner's rule, stated directly: **if the Resend API key is set, a failed send is a `Warning`; if the key is not set, the skip is an `Information`.** Severity tracks whether anything is actually wrong, not how much the operator might care.

There is a third case the rule did not name, resolved by the owner: when the send **throws** — transport failure, timeout, DNS — it stays `LogError(ex, ...)`. A provider rejection is the provider saying no, which is expected and actionable at Warning; an exception means something is broken and carries a stack trace, which is what `Error` is for. So the complete rule is:

| Condition | Level |
| --- | --- |
| No API key configured — send skipped | `Information` |
| Key configured, provider rejected the message (non-2xx) | `Warning` |
| Key configured, the send threw | `Error` (unchanged) |

`ResendEmailService.cs:96` and `:140` are the exception paths and must be left exactly as they are.

Four places currently violate it, in both repos:

| Location | Now | Should be | Why |
| --- | --- | --- | --- |
| `vord` `ResendEmailService.cs` invitation rejection (~`:90`) | `LogError` | `LogWarning` | Key is set, send was rejected |
| `vord` `ResendEmailService.cs` alert rejection (~`:134`) | `LogError` | `LogWarning` | Key is set, send was rejected |
| `vord-internal` `ResendEmailSender.cs` rejection | `LogError` | `LogWarning` | Key is set, send was rejected |
| `vord-internal` `NoOpEmailSender.cs` discard (~`:28`) | `LogWarning` | `LogInformation` | No key — a supported deployment, nothing is wrong |

The two skip paths in `vord`'s `ResendEmailService` are already `LogInformation` from Task 4b and need no further change.

**This partially reverses commit `57a92a3`**, whose stated purpose was escalating Resend rejections from Warning to Error so that a rejected send "produced signal that anyone would notice or alert on." That reasoning was sound when the log line was the *only* detection mechanism. It no longer is: the observability plan adds a `vord.email.send.failures` counter and a **critical** `VordEmailSendFailing` alert, so detection moves to the metric and the log level is free to describe severity honestly. Record that in the commit message so the reversal is not mistaken for a regression later.

**Files:**
- Modify: `vord/src/services.core/Services/Notifications/ResendEmailService.cs` — the two rejection log calls
- Modify: `vord-internal/src/billing-api/Services/ResendEmailSender.cs` — the rejection log call
- Modify: `vord-internal/src/billing-api/Services/NoOpEmailSender.cs` — the discard log call
- Test: `vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs`, `vord-internal/test/billing/Services/ResendEmailSenderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing new. Log severity only — no control flow, no return values, no signatures change.

- [ ] **Step 1: Update the assertions first**

Both repos have regression tests that assert `LogLevel.Error` on a rejected send and will fail once the level changes — that is the point, and they are the proof the change took effect.

In `vord/test/unit/services.core/Services/Notifications/ResendEmailServiceTests.cs`, the two tests added by `57a92a3` assert Error-level logging for `SendInvitationEmailAsync` and `SendAlertEmailAsync`. Change both to assert `LogLevel.Warning`, and update each test's name and doc comment so neither still claims "Error".

In `vord-internal/test/billing/Services/ResendEmailSenderTests.cs`, the rejection test asserts `r.Level == LogLevel.Error`. Change to `LogLevel.Warning` and rename accordingly.

Add one test per repo asserting the **no-key** path logs at `Information` and not at `Warning` — this is the half of the rule that currently has no coverage anywhere:

```csharp
    /// <summary>
    /// With no API key configured, skipping the send is an expected condition in a supported
    /// deployment, so it must not be logged as a fault.
    /// </summary>
    [Test]
    public async Task SendInvitationEmailAsync_NoApiKey_LogsAtInformation()
    {
        FakeLogger<ResendEmailService> logger = new();
        ResendEmailService service = CreateService(new ResendOptions(), logger);

        EmailDeliveryOutcome outcome = await service.SendInvitationEmailAsync(
            "to@example.com", "Tenant", "Inviter", "https://example.com/accept", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Skipped);
        await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsFalse();
        await Assert.That(logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Information)).IsTrue();
    }
```

Adapt `CreateService` to each file's existing construction helper rather than adding a new one.

Two further guards, carried over from Task 4b's re-review, which found the invariants it just established are unprotected:

Assert the enum's zero value, so a future alphabetise-or-reorder edit cannot silently put the optimistic outcome back in the `default` slot:

```csharp
    /// <summary>
    /// Skipped must hold the zero slot so a default-initialised or unstubbed value reads as
    /// "nothing was sent" rather than "the provider accepted the message".
    /// </summary>
    [Test]
    public async Task EmailDeliveryOutcome_DefaultIsSkipped()
    {
        await Assert.That(default(EmailDeliveryOutcome)).IsEqualTo(EmailDeliveryOutcome.Skipped);
    }
```

Put it in a new `test/unit/services.core/Services/Notifications/EmailDeliveryOutcomeTests.cs` following the file conventions of its neighbours.

The no-key `Information` tests specified above are themselves the second guard — before this task, nothing asserted the level on those paths, so a revert to `LogWarning` would have passed green.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/unit/services.core/unit.services.core.csproj --treenode-filter "/*/*/ResendEmailServiceTests/*"
```

Expected: the changed assertions fail because the code still logs at Error, and the new no-key tests fail on the `Warning` assertion for `NoOpEmailSender`'s equivalent in the other repo.

- [ ] **Step 3: Change the four log levels**

Change only the level on each of the four calls in the table above. Leave every message template, parameter, and structured field exactly as it is — the wording is already correct in all four places, and changing it would churn log-based queries for no benefit.

- [ ] **Step 4: Run the tests to verify they pass**

Build both solutions and run the affected suites. Because `dotnet run --no-build` can replay stale TUnit results in these repos, rebuild with `--no-incremental` and run the compiled executable for the final confirmation:

```bash
dotnet build machine-info.slnx --no-incremental
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

and in `vord-internal`:

```bash
dotnet build vord-internal.slnx -c Release --no-incremental
./test/billing/bin/Release/net10.0/osx-arm64/billing
```

- [ ] **Step 5: Sweep for any remaining violation**

Confirm no other email-related call site contradicts the rule:

```bash
command grep -rn "LogError\|LogWarning\|LogCritical" --include="*.cs" src | command grep -i -E "email|resend|invitation"
```

Every hit must be either a rejection with a key configured (`Warning`) or unrelated to email delivery. Report anything ambiguous rather than guessing.

- [ ] **Step 6: Commit**

```bash
git commit -m "Log a rejected send as a warning and an unconfigured one as information

Severity now tracks whether anything is actually wrong. A rejection with a
key configured is a warning; skipping with no key configured is a supported
deployment and only informational. This relaxes the Error level introduced
when the log line was the only detection mechanism — detection now comes
from the email-failure counter and its alert, not the log severity."
```

---

### Task 5: Full green build across both repos

**Files:** none modified.

**Interfaces:** none.

- [ ] **Step 1: Build and test vord-internal**

```bash
dotnet build vord-internal.slnx -c Release
dotnet run --project test/billing/billing.csproj
```

Expected: zero errors, zero warnings, all tests pass.

- [ ] **Step 2: Build and test vord**

```bash
dotnet build machine-info.slnx
dotnet run --project test/unit/services.core/unit.services.core.csproj
dotnet run --project test/unit/server/unit.server.csproj
dotnet run --project test/unit/database/unit.database.csproj
dotnet run --project test/functional/web/functional.web.csproj
dotnet run --project test/functional/grpc/functional.grpc.csproj
dotnet run --project test/functional/hangfire/functional.hangfire.csproj
```

Expected: zero errors, zero warnings, all suites pass. Any failure is root-caused and fixed here — never recorded as pre-existing and waved past.

- [ ] **Step 3: Type-check the frontends**

```bash
pnpm -C src/web check
```

Expected: exits 0 with no errors or warnings.

---

### Task 6: Push both repos

This is `vord-yp2` and it is the durability blocker on the release milestone. Ten commits sit unpushed on `vord` main, including `57a92a3` and `d012de0`, which carry the actual `vord-xoo` code fix. Until this runs, none of it exists in any built image.

**Files:** none modified.

**Interfaces:** none.

- [ ] **Step 1: Review exactly what is about to be published**

```bash
git -C vord log --oneline @{u}..
git -C vord-internal log --oneline @{u}..
git -C stack log --oneline @{u}..
```

Read the full list. Confirm every commit is intended for the remote and that no secrets, local-only `nuget.config` sources, or scratch files are included.

- [ ] **Step 2: Confirm with the repository owner before pushing**

Pushing is outward-facing and the repo's agent profile is Conservative. Present the commit list from Step 1 and get explicit approval.

- [ ] **Step 3: Push**

```bash
git -C vord push
git -C vord-internal push
git -C stack push
```

- [ ] **Step 4: Verify the images build**

Watch CI for both repos. The fix only reaches production once a new image is published and deployed.

---

### Task 7: Prove delivery end to end

**Files:** none modified.

**Interfaces:** none.

- [ ] **Step 1: Confirm the deployed configmap carries the verified sender**

```bash
kubectl -n vord-fleet get configmap -o yaml | grep -A1 "Resend__FromEmail"
```

Expected: `Framlux Vord <invitations@outreach.framlux.io>` for the fleet config and `Framlux Vord Billing <alerts@outreach.framlux.io>` for the billing config. Commit `1ec5c3f` is pushed but its ArgoCD sync has never been verified — if the values are stale, check the ArgoCD application status before going further.

- [ ] **Step 2: Send a real invitation**

From the running fleet app, invite an address on a mailbox you control and that is external to the Resend account.

- [ ] **Step 3: Confirm receipt and capture evidence**

Confirm the email arrives. Record the Resend message ID, the recipient domain, and the date.

- [ ] **Step 4: Confirm the logs are clean**

```bash
kubectl -n vord-fleet logs deploy/api-server --since=10m | grep -i "resend"
```

Expected: an informational send record, and no `550`, no `not verified`, no Error-level Resend line.

- [ ] **Step 5: Record the evidence on the bead**

```bash
bd update vord-xoo --notes "Delivered end to end on <date>. Resend message ID <id>, recipient domain <domain>. Configmap verified in-cluster."
```

- [ ] **Step 6: Leave vord-xoo open**

Do not close it yet. Its second exit criterion — a guard that fails loudly rather than dropping mail silently — is satisfied by the `vord.email.send.failures` metric and its critical alert in the observability plan. Closing happens there.

---

### Task 8: File the sender-migration follow-up

**Files:** none modified.

**Interfaces:** none.

- [ ] **Step 1: Create the bead**

```bash
bd create "Move transactional email to a verified vordfleet.dev sender" \
  -p 2 \
  -d "PROBLEM: every sender across the estate is on outreach.framlux.io because it is the only domain verified on the current single-domain Resend plan. Transactional invitation mail from an 'outreach' subdomain reads as marketing to both recipients and spam filters, and shares sending reputation with actual outreach traffic.

APPROACH: once paying customers justify the paid Resend tier, verify vordfleet.dev (SPF, DKIM, DMARC records), then move senders over.

SCOPE: fleet Resend__FromEmail and billing Resend__FromEmail in stack clusters/prod/apps/vord-platform/base/kustomization.yaml, AND Alertmanager's smtp_from in clusters/prod/apps/observability/base/alertmanager/alertmanager.yml plus its copy in tests/alertmanager/alertmanager.yml. Missing the Alertmanager sender breaks paging.

EXIT: all senders on vordfleet.dev, delivery confirmed for an invitation and an Alertmanager notification.

NOTE: post-launch. Deliberately not a vord-3ta blocker."
```

- [ ] **Step 2: Link it as related, not blocking**

```bash
bd dep add <new-id> vord-3ta --type related
```

Confirm with `bd dep tree vord-3ta` that the release milestone did not gain a new blocker.

---

## Verification

`vord-xoo` is ready to close — pending only the observability plan's email alert — when all of the following hold:

- `billing-api` refuses to start with a Resend API key and no sender.
- A rejected send logs the Resend status code and response body at Error in both repos.
- A failed canary notification no longer discards the canary health record.
- Both repos build with zero warnings and every suite passes.
- All commits are pushed and images are built.
- A real invitation has been delivered, with the message ID recorded on the bead.
- The follow-up bead exists and is linked as related, not blocking.
