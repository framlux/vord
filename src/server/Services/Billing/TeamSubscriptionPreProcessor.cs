// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// FastEndpoints global pre-processor that gates endpoints tagged
/// <see cref="EndpointTags.RequiresTeamSubscription"/> behind the Team tier. Modelled on
/// <see cref="ProSubscriptionPreProcessor"/>, which already solved the same problem for Pro
/// features; this replaces the hand-written copies of the Team check that each gated handler used
/// to carry.
/// </summary>
/// <remarks>
/// <para>
/// This type must stay registered in the FastEndpoints configurator, <b>after</b> the Pro
/// pre-processor. A tag is inert metadata, so an unregistered pre-processor fails silently and
/// open: every tagged endpoint becomes available to every tier, with nothing failing to compile.
/// The registration is covered by functional tests using fixtures that only the Team gate can
/// refuse.
/// </para>
/// <para>
/// The order relative to Pro is observable, not cosmetic. The alert-rule endpoints carry both tags,
/// and a Free tenant is meant to see the Pro message; running Team first would change which message
/// that tenant receives.
/// </para>
/// </remarks>
public sealed class TeamSubscriptionPreProcessor : IGlobalPreProcessor
{
    /// <summary>
    /// The feature-neutral message returned when a gated endpoint has not attached a
    /// <see cref="RequiresTeamFeatureMessage"/> describing its own feature.
    /// </summary>
    public const string DefaultRequiresTeamMessage = "This feature requires a Team subscription";

    /// <inheritdoc/>
    public async Task PreProcessAsync(IPreProcessorContext context, CancellationToken ct)
    {
        HttpContext httpContext = context.HttpContext;

        // An earlier pre-processor may already have written a 403. Do not write again — a second
        // write corrupts the response stream.
        if (httpContext.ResponseStarted())
        {
            return;
        }

        Endpoint? endpoint = httpContext.GetEndpoint();
        EndpointDefinition? epDef = endpoint?.Metadata?.GetMetadata<EndpointDefinition>();
        if ((epDef is null) || epDef.EndpointTags?.Contains(EndpointTags.RequiresTeamSubscription) != true)
        {
            return;
        }

        // No tenant context — defer to the endpoint, which emits its own 401.
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(httpContext.User, httpContext);
        if (tenantId is null)
        {
            return;
        }

        ISubscriptionService subscriptionService = httpContext.RequestServices
            .GetRequiredService<ISubscriptionService>();

        TenantSubscription? subscription = await subscriptionService
            .GetSubscriptionForTenantAsync(tenantId.Value, ct);

        if (SubscriptionPolicy.RequiresTeam(subscription))
        {
            RequiresTeamFeatureMessage? featureMessage = endpoint?.Metadata?.GetMetadata<RequiresTeamFeatureMessage>();
            string message = ResolveMessage(featureMessage);

            await httpContext.SendApiErrorAsync(403, message, ct);

            context.HttpContext.MarkResponseStart();
        }
    }

    /// <summary>
    /// Selects the 403 message to return: the gated endpoint's own
    /// <see cref="RequiresTeamFeatureMessage"/> when it attached one, otherwise the feature-neutral
    /// <see cref="DefaultRequiresTeamMessage"/>. Extracted as an <c>internal static</c> method so
    /// message selection can be unit-tested directly.
    /// </summary>
    /// <param name="featureMessage">The endpoint's attached feature message metadata, if any.</param>
    /// <returns>The message to return in the 403 response body.</returns>
    internal static string ResolveMessage(RequiresTeamFeatureMessage? featureMessage)
    {
        return featureMessage?.Message ?? DefaultRequiresTeamMessage;
    }
}
