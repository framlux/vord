// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// One revision of an enterprise agreement as the fleet applies it: every limit is explicit, and
/// unlimited is <see cref="int.MaxValue"/>.
/// </summary>
/// <param name="TenantId">The tenant the agreement covers.</param>
/// <param name="AgreementId">The billing system's identifier for the agreement.</param>
/// <param name="Revision">The agreement revision; a higher number supersedes a lower one.</param>
/// <param name="MachineLimit">Machines the tenant may register.</param>
/// <param name="RetentionDays">Days of telemetry retained.</param>
/// <param name="MemberLimit">Members the tenant may have, including pending invitations.</param>
/// <param name="AlertRuleLimit">Custom alert rules the tenant may have; zero means none.</param>
/// <param name="WebhookLimit">Webhook integrations the tenant may have; zero means none.</param>
/// <param name="TermEnd">The end of the agreement term.</param>
public sealed record EnterpriseAgreementTerms(
    int TenantId,
    long AgreementId,
    int Revision,
    int MachineLimit,
    int RetentionDays,
    int MemberLimit,
    int AlertRuleLimit,
    int WebhookLimit,
    DateTimeOffset TermEnd);
