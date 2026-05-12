// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Records an HTTP request for assertion purposes.
/// </summary>
public sealed class RecordedRequest
{
    /// <summary>The request URL.</summary>
    public required Uri? RequestUri { get; init; }

    /// <summary>The HTTP method.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>The request headers.</summary>
    public required Dictionary<string, IEnumerable<string>> Headers { get; init; }

    /// <summary>The request body.</summary>
    public required string? Body { get; init; }
}

/// <summary>
/// A reusable <see cref="DelegatingHandler"/> that records requests and returns configurable responses.
/// </summary>
public sealed class MockHttpMessageHandler : DelegatingHandler
{
    private readonly Dictionary<string, HttpResponseMessage> _responseMap = new(StringComparer.OrdinalIgnoreCase);
    private HttpResponseMessage _defaultResponse = new(System.Net.HttpStatusCode.OK);
    private Exception? _exception;

    /// <summary>All recorded requests.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>
    /// Sets the default response returned for any request that does not match a per-URL mapping.
    /// </summary>
    public MockHttpMessageHandler WithDefaultResponse(HttpResponseMessage response)
    {
        _defaultResponse = response;

        return this;
    }

    /// <summary>
    /// Adds a per-URL response mapping.
    /// </summary>
    public MockHttpMessageHandler WithResponse(string url, HttpResponseMessage response)
    {
        _responseMap[url] = response;

        return this;
    }

    /// <summary>
    /// Configures the handler to throw the given exception on any request.
    /// </summary>
    public MockHttpMessageHandler WithException(Exception exception)
    {
        _exception = exception;

        return this;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        Dictionary<string, IEnumerable<string>> headers = new();
        foreach (System.Collections.Generic.KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers[header.Key] = header.Value;
        }

        Requests.Add(new RecordedRequest
        {
            RequestUri = request.RequestUri,
            Method = request.Method,
            Headers = headers,
            Body = body,
        });

        if (_exception is not null)
        {
            throw _exception;
        }

        string url = request.RequestUri?.ToString() ?? string.Empty;
        if (_responseMap.TryGetValue(url, out HttpResponseMessage? mapped))
        {
            return mapped;
        }

        return _defaultResponse;
    }
}
