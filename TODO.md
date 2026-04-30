# TODO

## Alerts

- **Machine-scoping on alert rules** — Currently all alert rules evaluate against every machine in the tenant. Add support for targeting rules to specific machines or groups of machines (e.g., by tag, group, or explicit machine list). Requires: optional `MachineId` column on `AlertRule` (NULL = all machines), or a join table `AlertRuleMachines` for multi-machine targeting. Update `AlertEvaluationService.EvaluateAllRulesAsync` to filter machine states by rule scope. Add frontend UI for selecting target machines during rule creation/editing.
