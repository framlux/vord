// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="ResendEmailService"/>.
/// </summary>
public sealed class ResendEmailServiceTests
{
    private static IOptions<ResendOptions> BuildOptions(string? apiKey = null)
    {
        return Options.Create(new ResendOptions
        {
            ApiKey = apiKey ?? string.Empty,
            FromEmail = "Test <test@vordfleet.dev>",
        });
    }

    [Test]
    public async Task SendInvitation_NoApiKey_ReturnsFalse()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: null);

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsFalse();
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SendInvitation_EmptyApiKey_ReturnsFalse()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsFalse();
        await Assert.That(handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SendInvitation_ValidApiKey_PostsToResendApi()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_123");

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
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_456");

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
    public async Task SendInvitation_ResendReturns200_ReturnsTrue()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_ok");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task SendInvitation_ResendReturns400_ReturnsFalse()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bad request"),
        });
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_400");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task SendInvitation_ResendReturns500_ReturnsFalse()
    {
        MockHttpMessageHandler handler = new();
        handler.WithDefaultResponse(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server error"),
        });
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_500");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task SendInvitation_HttpException_ReturnsFalse()
    {
        MockHttpMessageHandler handler = new();
        handler.WithException(new HttpRequestException("Connection failed"));
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_err");

        ResendEmailService service = new(httpClient, options, new NullLogger<ResendEmailService>());

        bool result = await service.SendInvitationEmailAsync("user@example.com", "Acme", "Admin", "https://app.example.com/accept", CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task SendInvitation_HtmlEncodesUserInputs()
    {
        MockHttpMessageHandler handler = new();
        HttpClient httpClient = new(handler);
        IOptions<ResendOptions> options = BuildOptions(apiKey: "re_test_xss");

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
}
