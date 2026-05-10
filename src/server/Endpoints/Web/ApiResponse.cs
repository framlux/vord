// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web;

/// <summary>
/// Standard API response wrapper for consistent response format.
/// </summary>
public class ApiResponse<T>
{
    /// <summary>
    /// Indicates whether the request was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The response data.
    /// </summary>
    public T? Data { get; set; }

    /// <summary>
    /// A message describing the result.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// List of errors if the request failed.
    /// </summary>
    public List<string>? Errors { get; set; }

    /// <summary>
    /// Machine-readable error code for programmatic handling by API consumers.
    /// Only present on error responses. Examples: SUBSCRIPTION_REQUIRED, ALERT_LIMIT_EXCEEDED.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Creates a successful response.
    /// </summary>
    public static ApiResponse<T> Ok(T data, string? message = null)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Message = message
        };
    }

    /// <summary>
    /// Creates an error response.
    /// </summary>
    public static ApiResponse<T> Error(string message, List<string>? errors = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Message = message,
            Errors = errors
        };
    }

    /// <summary>
    /// Creates an error response with a structured error code.
    /// </summary>
    public static ApiResponse<T> Error(string message, string errorCode, List<string>? errors = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Message = message,
            ErrorCode = errorCode,
            Errors = errors
        };
    }
}
