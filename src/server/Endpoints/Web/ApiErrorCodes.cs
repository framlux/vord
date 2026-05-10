// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web;

/// <summary>
/// Machine-readable error codes for API consumers.
/// Use these with <see cref="ApiResponse{T}.Error(string, string, List{string}?)"/> to provide
/// structured error codes alongside human-readable messages.
/// </summary>
public static class ApiErrorCodes
{
    /// <summary>Tenant subscription is required for this action.</summary>
    public const string SubscriptionRequired = "SUBSCRIPTION_REQUIRED";

    /// <summary>The subscription tier does not include this feature.</summary>
    public const string TierInsufficient = "TIER_INSUFFICIENT";

    /// <summary>A resource limit for the subscription tier has been reached.</summary>
    public const string LimitReached = "LIMIT_REACHED";

    /// <summary>The caller is not authenticated.</summary>
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>The caller does not have permission for this action.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>The requested resource was not found.</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>The request conflicts with existing state.</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>The request contains invalid input.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>The subscription is canceled; account is read-only.</summary>
    public const string SubscriptionCanceled = "SUBSCRIPTION_CANCELED";

    /// <summary>An internal server error occurred.</summary>
    public const string InternalError = "INTERNAL_ERROR";
}
