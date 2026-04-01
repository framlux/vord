// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Represents the result of a service operation with an HTTP status code and optional data.
/// </summary>
/// <typeparam name="T">The type of the response data.</typeparam>
public sealed class ServiceResult<T>
{
    /// <summary>
    /// The HTTP status code representing the outcome.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// The response data, if any.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// An optional error message describing what went wrong.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the result represents a not-found outcome.
    /// </summary>
    public bool IsNotFound => StatusCode == 404;

    /// <summary>
    /// Whether the result represents a successful outcome.
    /// </summary>
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    /// <summary>
    /// Creates a successful result with data.
    /// </summary>
    public static ServiceResult<T> Ok(T data) =>
        new() { StatusCode = 200, Data = data };

    /// <summary>
    /// Creates a not-found result.
    /// </summary>
    public static ServiceResult<T> NotFound() =>
        new() { StatusCode = 404, Data = default };

    /// <summary>
    /// Creates an error result with the specified status code and data.
    /// </summary>
    public static ServiceResult<T> Error(int statusCode, T data) =>
        new() { StatusCode = statusCode, Data = data };

    /// <summary>
    /// Creates a validation error result with a message.
    /// </summary>
    public static ServiceResult<T> BadRequest(string errorMessage) =>
        new() { StatusCode = 400, Data = default, ErrorMessage = errorMessage };
}
