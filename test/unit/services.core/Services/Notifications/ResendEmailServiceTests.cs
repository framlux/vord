// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ResendEmailService"/>. This transport is only registered when a Resend
/// API key is present, so every case here assumes one; a deployment with no transport at all is
/// covered by the no-op service instead.
/// </summary>
public sealed class ResendEmailServiceTests
{
    private static IOptions<EmailOptions> BuildOptions(string apiKey)
    {
        return Options.Create(new EmailOptions
        {
            FromEmail = "Test <test@outreach.framlux.io>",
            Resend = new ResendEmailOptions { ApiKey = apiKey },
        });
    }

    [Test]
    public async Task SendInvitation_ValidApiKey_PostsToResendApi()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_123");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        await Assert.That(handler.Requests[0].RequestUri!.ToString()).IsEqualTo("https://api.resend.com/emails");
        await Assert.That(handler.Requests[0].Method).IsEqualTo(HttpMethod.Post);

        IEnumerable<string> authValues = handler.Requests[0].Headers["Authorization"];
        string authHeader = authValues.First();
        await Assert.That(authHeader).IsEqualTo("Bearer re_test_123");
    }

    [Test]
    public async Task SendInvitation_ValidApiKey_RequestBodyContainsFields()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_456");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        await service.SendInvitationEmailAsync("invite@example.com", "Acme Corp", "Jane", "https://app.example.com/accept/token123", CancellationToken.None);

        string? body = handler.Requests[0].Body;
        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("\"from\":");
        await Assert.That(body).Contains("\"to\":");
        await Assert.That(body).Contains("\"subject\":");
        await Assert.That(body).Contains("\"html\":");
        await Assert.That(body).Contains("invite@example.com");
    }

    [Test]
    public async Task SendInvitation_ResendReturns200_ReturnsSent()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_ok");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Sent);
    }

    [Test]
    public async Task SendInvitation_ResendReturns400_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad request"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_400");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendInvitation_ResendReturns500_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_500");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendInvitation_ResendReturns550_LogsWarningWithStatusCodeAndBody()
    {
        // Regression test for vord-xoo: a rejected send (e.g. Resend's 550 for an
        // unverified sender domain) must be logged at Warning, not swallowed or logged
        // below Warning, so it is visible in logs. Detection of the failure itself now
        // comes from the email-failure counter and its alert, not the log severity.
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage((System.Net.HttpStatusCode)550)
        {
            Content = new StringContent("The vordfleet.dev domain is not verified"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_550");
        ILogger<ResendEmailService> logger = Substitute.For<ILogger<ResendEmailService>>();

        ResendEmailService service = new(httpClient, options, logger);

        EmailDeliveryOutcome result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task SendInvitation_HttpException_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection failed"));
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_err");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendInvitation_HtmlEncodesUserInputs()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_test_xss");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        await service.SendInvitationEmailAsync("user@example.com", "<script>alert('xss')</script>", "Admin", "https://app.example.com/accept", CancellationToken.None);

        string? body = handler.Requests[0].Body;
        await Assert.That(body).IsNotNull();

        using JsonDocument doc = JsonDocument.Parse(body!);
        string htmlField = doc.RootElement.GetProperty("html").GetString()!;

        // The HTML body must not contain raw script tags from user input
        await Assert.That(htmlField.Contains("<script>alert")).IsFalse();
        // The HTML body must contain the HTML-encoded form of the tenant name
        await Assert.That(htmlField).Contains("&lt;script&gt;");
    }

    [Test]
    public async Task SendAlertEmail_ValidApiKey_ReturnsSent()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_ok");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendAlertEmailAsync("alert@example.com", "Alert: Vehicle offline", "<p>Your vehicle is offline.</p>", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Sent);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SendAlertEmail_ResendReturns500_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_500");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendAlertEmailAsync("alert@example.com", "Alert", "<p>Body</p>", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendAlertEmail_ResendReturns400_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad request"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_400");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendAlertEmailAsync("alert@example.com", "Alert", "<p>Body</p>", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendAlertEmail_HttpException_ReturnsFailed()
    {
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection failed"));
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_err");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        EmailDeliveryOutcome result = await service.SendAlertEmailAsync("alert@example.com", "Alert", "<p>Body</p>", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendAlertEmail_ValidApiKey_PostsExpectedRequest()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_shape");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        await service.SendAlertEmailAsync("alert@example.com", "Alert: Vehicle offline", "<p>Your vehicle is offline.</p>", CancellationToken.None);

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        RecordedRequest request = handler.Requests[0];

        await Assert.That(request.RequestUri!.ToString()).IsEqualTo("https://api.resend.com/emails");
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);

        IEnumerable<string> authValues = request.Headers["Authorization"];
        string authHeader = authValues.First();
        await Assert.That(authHeader).IsEqualTo("Bearer re_alert_shape");

        string? body = request.Body;
        await Assert.That(body).IsNotNull();
        await Assert.That(body!).Contains("\"from\":");
        await Assert.That(body).Contains("\"to\":");
        await Assert.That(body).Contains("\"subject\":");
        await Assert.That(body).Contains("\"html\":");
        await Assert.That(body).Contains("alert@example.com");
    }

    [Test]
    public async Task SendAlertEmail_ResendReturns550_LogsWarningWithStatusCodeAndBody()
    {
        // Regression test for vord-xoo: a rejected send (e.g. Resend's 550 for an
        // unverified sender domain) must be logged at Warning, not swallowed or logged
        // below Warning, so it is visible in logs. Detection of the failure itself now
        // comes from the email-failure counter and its alert, not the log severity.
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage((System.Net.HttpStatusCode)550)
        {
            Content = new StringContent("The vordfleet.dev domain is not verified"),
        });
        HttpClient httpClient = new(handler);
        IOptions<EmailOptions> options = BuildOptions("re_alert_550");
        ILogger<ResendEmailService> logger = Substitute.For<ILogger<ResendEmailService>>();

        ResendEmailService service = new(httpClient, options, logger);

        EmailDeliveryOutcome result = await service.SendAlertEmailAsync("alert@example.com", "Alert", "<p>Body</p>", CancellationToken.None);

        await Assert.That(result).IsEqualTo(EmailDeliveryOutcome.Failed);
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task Constructor_NullHttpClient_Throws()
    {
        await Assert.That(() => new ResendEmailService(
                null!, Options.Create(new EmailOptions()), NullLogger<ResendEmailService>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullEmailOptions_Throws()
    {
        using HttpClient httpClient = new();

        await Assert.That(() => new ResendEmailService(httpClient, null!, NullLogger<ResendEmailService>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        using HttpClient httpClient = new();

        await Assert.That(() => new ResendEmailService(httpClient, Options.Create(new EmailOptions()), null!))
            .Throws<ArgumentNullException>();
    }
}
