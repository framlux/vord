# End-to-End Testing Guide — Core Application

This runbook covers manual end-to-end testing of the Vord Fleet platform (server, web, agent, infrastructure). Run through these tests before each release to validate the full stack.

For billing/Stripe-specific testing, see [e2e-testing-billing.md](e2e-testing-billing.md).

---

## Table of Contents

1. [Prerequisites & Environment Setup](#1-prerequisites--environment-setup)
2. [Infrastructure Health](#2-infrastructure-health)
3. [Authentication & Onboarding](#3-authentication--onboarding)
4. [Tenant Management](#4-tenant-management)
5. [Member & Invitation Management](#5-member--invitation-management)
6. [Registration Tokens & Machine Registration](#6-registration-tokens--machine-registration)
7. [Agent Communication (gRPC)](#7-agent-communication-grpc)
8. [Machine Management](#8-machine-management)
9. [Signing Keys & Remote Commands](#9-signing-keys--remote-commands)
10. [Alerts & Webhooks (Pro/Team Tier)](#10-alerts--webhooks-proteam-tier)
11. [Subscription Limits](#11-subscription-limits)
12. [Data Export](#12-data-export)
13. [Dashboard & Audit](#13-dashboard--audit)
14. [Web UI Walkthrough](#14-web-ui-walkthrough)
15. [Authorization & Access Control](#15-authorization--access-control)
16. [Teardown](#16-teardown)

---

## 1. Prerequisites & Environment Setup

### Path A — Docker Compose (Release Validation)

Use this path to validate published container images before a release.

1. Navigate to the Docker Compose directory:

   ```bash
   cd deployment/server/docker/
   ```

2. Copy the example environment file and fill in required values:

   ```bash
   cp .env.example .env
   ```

3. Edit `.env` and configure at minimum:
   - `DB_USER`, `DB_PASSWORD`, `DB_NAME` — PostgreSQL credentials
   - `CORS_ORIGIN`, `APP_BASE_URL` — your local URL (e.g., `https://localhost:5254`)
   - OAuth credentials for at least one provider:
     - `GITHUB_CLIENT_ID` / `GITHUB_CLIENT_SECRET`
     - `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`
     - `MICROSOFT_CLIENT_ID` / `MICROSOFT_CLIENT_SECRET`
   - `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`, `OIDC_METADATA_ADDRESS`
   - `INTERNAL_API_KEY` — shared secret for inter-service auth
   - `RESEND_API_KEY`, `RESEND_FROM_EMAIL` — for invitation emails (optional for core testing)

4. Start all services:

   ```bash
   docker compose up -d
   ```

5. Wait for health checks to pass (migration runner completes first, then API server, then web):

   ```bash
   docker compose ps
   ```

   All services should show `healthy` status.

**Service ports (defaults):**

| Service          | Port  | Protocol |
|------------------|-------|----------|
| API Server HTTP  | 12233 | HTTP     |
| API Server gRPC  | 12234 | gRPC     |
| Web UI           | 5254  | HTTP     |

### Path B — Local Build (Dev Verification)

Use this path for pre-commit validation during development.

1. Start infrastructure dependencies (Postgres + Redis):

   ```bash
   # Option 1: Use Docker for just the infrastructure
   cd deployment/server/docker/
   docker compose up -d postgres redis

   # Option 2: Use locally installed Postgres and Redis
   ```

2. Run database migrations:

   ```bash
   dotnet run --project src/migrationRunner/migrationRunner.csproj
   ```

3. Start the API server:

   ```bash
   dotnet run --project src/server/server.csproj
   ```

4. Start the web UI (in a separate terminal):

   ```bash
   cd src/web/
   pnpm dev
   ```

   The web UI runs on `https://localhost:5173` in dev mode (with HTTPS via local certs in `src/web/certs/`). The Vite dev server proxies `/api` requests to `http://127.0.0.1:12233`.

---

## 2. Infrastructure Health

Verify all services are running and dependencies are connected.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 2.1 | `GET /healthz` on API server (port 12233) | Returns `Healthy` (HTTP 200) | ☐ |
| 2.2 | `GET /readyz` on API server (port 12233) | Returns `Healthy` with Postgres and Redis checks passing (HTTP 200) | ☐ |
| 2.3 | `GET /healthz` on Web UI (port 5254 or 5173) | Returns healthy response (HTTP 200) | ☐ |
| 2.4 | Verify Redis is reachable | `redis-cli ping` returns `PONG` | ☐ |
| 2.5 | Verify Postgres is reachable | `pg_isready -U <DB_USER> -d <DB_NAME>` returns accepting connections | ☐ |

**Example commands:**

```bash
# API health checks
curl -s http://localhost:12233/healthz
curl -s http://localhost:12233/readyz

# Web health check
curl -s http://localhost:5254/healthz

# Redis
redis-cli ping

# Postgres
pg_isready -U vordfleet -d vordfleet
```

---

## 3. Authentication & Onboarding

Test the full sign-in flow via OAuth providers and first-time user onboarding.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 3.1 | Navigate to `/auth/login` in browser | Login page renders with OAuth provider buttons (GitHub, Google, Microsoft) | ☐ |
| 3.2 | Click a configured OAuth provider button | Redirected to provider's consent screen | ☐ |
| 3.3 | Complete OAuth sign-in (first-time user) | Redirected to `/onboarding` page | ☐ |
| 3.4 | Complete onboarding: enter organization name, submit | Organization created; redirected to `/onboarding/success` then `/dashboard` | ☐ |
| 3.5 | `GET /api/v1/auth/me` (with session cookie) | Returns JSON with user profile (id, email, name, tenant info) | ☐ |
| 3.6 | Navigate to `/auth/logout` | Session destroyed (`vord_auth` cookie cleared); redirected to `/auth/login` | ☐ |
| 3.7 | Sign in again (returning user) | Redirected directly to `/dashboard` (skips onboarding) | ☐ |

**Notes:**
- The session cookie is named `vord_auth`, HTTP-only, SameSite=Lax, Secure=Always
- Cookie expiry is 7 days with sliding expiration
- At least one OAuth provider must be configured for testing

---

## 4. Tenant Management

Test creating, listing, viewing, and switching tenants.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 4.1 | `POST /api/v1/tenants` with `{ "name": "Test Tenant" }` | Returns 201 with new tenant details (id, name) | ☐ |
| 4.2 | `GET /api/v1/tenants` | Returns list of tenants the current user belongs to; includes newly created tenant | ☐ |
| 4.3 | `GET /api/v1/tenants/{tenantId}` | Returns tenant details (name, subscription info, member count) | ☐ |
| 4.4 | `POST /api/v1/tenants/{tenantId}/switch` | Active tenant switched; subsequent API calls reflect new tenant context | ☐ |
| 4.5 | Verify `GET /api/v1/auth/me` reflects the switched tenant | User profile shows updated active tenant | ☐ |

---

## 5. Member & Invitation Management

Test inviting users, accepting invitations, changing roles, and removing members.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 5.1 | `POST /api/v1/invitations` with `{ "email": "test@example.com", "role": "Viewer" }` | Returns 201 with invitation details | ☐ |
| 5.2 | `GET /api/v1/invitations` | Returns list of pending invitations; includes the one just created | ☐ |
| 5.3 | `GET /api/v1/invitations/{invitationId}` | Returns invitation details (email, role, status, expiry) | ☐ |
| 5.4 | `POST /api/v1/invitations/{invitationId}/resend` | Invitation email resent; returns success | ☐ |
| 5.5 | Accept invitation: sign in as invited user, navigate to `/invitations/accept?token=<token>` | Invitation accepted; user added to tenant with assigned role | ☐ |
| 5.6 | `GET /api/v1/invitations/members` | Returns list of tenant members; includes newly accepted member | ☐ |
| 5.7 | `PUT /api/v1/invitations/members/{memberId}/role` with `{ "role": "MachineAdmin" }` | Member role updated | ☐ |
| 5.8 | `DELETE /api/v1/invitations/members/{memberId}` | Member removed from tenant | ☐ |
| 5.9 | `DELETE /api/v1/invitations/{invitationId}` (on a pending invitation) | Invitation revoked | ☐ |

**Roles available:** `TenantAdmin`, `MachineAdmin`, `Viewer`

---

## 6. Registration Tokens & Machine Registration

Test creating registration tokens and using them to register machines.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 6.1 | `POST /api/v1/tenants/{tenantId}/registration-token` | Returns 201 with token value and metadata | ☐ |
| 6.2 | `GET /api/v1/tenants/{tenantId}/registration-tokens` | Returns list of registration tokens; includes newly created token | ☐ |
| 6.3 | Use token to register a machine (see [Section 7.1](#7-agent-communication-grpc) for gRPC registration) | Machine registered; receives unique API key | ☐ |
| 6.4 | `GET /api/v1/machines` | Newly registered machine appears in machine list | ☐ |
| 6.5 | `DELETE /api/v1/tenants/{tenantId}/registration-token/{tokenId}` | Token revoked; further registrations with this token are rejected | ☐ |
| 6.6 | Attempt registration with revoked token | Registration rejected with appropriate error | ☐ |

**Agent installation (on a Linux machine):**

```bash
curl -fsSL https://get.vordfleet.dev/install.sh | sudo bash
```

The install script prompts for:
- Server address (default: `grpc.vordfleet.dev:443`)
- Registration token (required, from step 6.1)

Configuration is stored at `/etc/framlux/vord-agent.toml`. The agent runs as a systemd service (`vord-agent`).

---

## 7. Agent Communication (gRPC)

Test gRPC services on port 12234. Use a gRPC client such as [grpcurl](https://github.com/fullstorydev/grpcurl) or [Evans](https://github.com/ktr0731/evans).

All gRPC calls (except registration) require the `x-api-key` metadata header with the machine's API key.

### 7.1 Registration

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 7.1.1 | Call `Registration.RegisterSystem` with registration token, hostname, machine type, OS type | Returns `machine_id` and `api_key` | ☐ |
| 7.1.2 | Call `Registration.GetRegistrationStatus` with the returned API key | Returns `REGISTRATION_ACTIVE` | ☐ |

**Example grpcurl (registration):**

```bash
grpcurl -plaintext \
  -d '{
    "registration_token": "<token>",
    "hostname": "test-machine-01",
    "machine_type": "VIRTUAL_MACHINE",
    "operating_system_type": "UBUNTU"
  }' \
  localhost:12234 registration.Registration/RegisterSystem
```

**Machine types:** `DESKTOP`, `LAPTOP`, `BARE_METAL_SERVER`, `VIRTUAL_MACHINE`
**OS types:** `WINDOWS`, `MAC`, `UBUNTU`, `FEDORA`, `REDHAT`

### 7.2 Configuration & Ping

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 7.2.1 | Call `Configuration.AgentPing` with API key | Returns success response | ☐ |
| 7.2.2 | Call `Configuration.GetConfiguration` with API key | Returns configuration: heartbeat interval, config refresh interval, command poll interval (default 30s), trusted signing keys, signing key version | ☐ |

### 7.3 Telemetry Submission

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 7.3.1 | Call `Telemetry.SubmitTelemetry` with a `TelemetryEnvelope` containing `SYSTEM_INFO_TYPE` payload | Returns `TelemetryAck` with success | ☐ |
| 7.3.2 | `GET /api/v1/machines/{machineId}/telemetry/latest` | Returns the submitted telemetry data | ☐ |
| 7.3.3 | Call `Telemetry.StreamTelemetry` (bidirectional stream) with multiple telemetry types | Each envelope acknowledged via stream | ☐ |

**Telemetry types available:**

| Type | Description |
|------|-------------|
| `SYSTEM_INFO_TYPE` | Hostname, CPU, memory, hardware info |
| `OS_VERSION_TYPE` | OS name, version, build |
| `CPU_INFO_TYPE` | CPU model, cores, speed |
| `MEMORY_INFO_TYPE` | RAM, swap info |
| `DISK_INFO_TYPE` | Disk devices, mount points, sizes |
| `CPU_UTILIZATION_TYPE` | CPU usage percentage and breakdown |
| `MEMORY_UTILIZATION_TYPE` | Memory usage stats |
| `DISK_UTILIZATION_TYPE` | Disk usage by mount |
| `SSH_SESSION_TYPE` | SSH session info (user, source IP, port, auth method) |
| `HARDWARE_HEALTH_TYPE` | Fans, power supplies, temperatures, disk SMART |
| `PACKAGE_UPDATES_TYPE` | Available package updates |
| `SERVICE_STATUS_TYPE` | Systemd service status |

### 7.4 Commands

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 7.4.1 | Call `Configuration.GetPendingCommands` with API key | Returns list of pending commands (empty if none queued) | ☐ |
| 7.4.2 | Send a remote command via REST (see [Section 9](#9-signing-keys--remote-commands)), then call `GetPendingCommands` | Returns the queued command | ☐ |
| 7.4.3 | Call `Configuration.AcknowledgeCommand` with command ID | Command acknowledged; status updated | ☐ |

---

## 8. Machine Management

Test viewing, searching, and deleting machines via the REST API.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 8.1 | `GET /api/v1/machines` | Returns paginated machine list (default page size) | ☐ |
| 8.2 | `GET /api/v1/machines?search=test-machine&page=1&pageSize=10` | Returns filtered results matching search term | ☐ |
| 8.3 | `GET /api/v1/machines?os=UBUNTU&status=online` | Returns machines filtered by OS and status | ☐ |
| 8.4 | `GET /api/v1/machines/{machineId}` | Returns machine detail (hostname, type, OS, registration date) | ☐ |
| 8.5 | `GET /api/v1/machines/{machineId}/full` | Returns machine detail with full telemetry data | ☐ |
| 8.6 | `GET /api/v1/machines/{machineId}/status` | Returns machine health status | ☐ |
| 8.7 | `GET /api/v1/machines/{machineId}/certificates` | Returns machine certificates | ☐ |
| 8.8 | `GET /api/v1/machines/{machineId}/telemetry` | Returns telemetry history | ☐ |
| 8.9 | `GET /api/v1/machines/{machineId}/telemetry/latest` | Returns most recent telemetry snapshot | ☐ |
| 8.10 | `GET /api/v1/machines/ssh-sessions/fleet` | Returns SSH sessions across all machines in the tenant | ☐ |
| 8.11 | `DELETE /api/v1/machines/{machineId}` | Machine deleted; no longer appears in machine list | ☐ |

**Query parameters for `GET /api/v1/machines`:**
- `page` — Page number (default: 1)
- `pageSize` — Items per page
- `search` — Search by hostname
- `os` — Filter by OS type
- `type` — Filter by machine type
- `status` — Filter by online/offline status
- `sortBy` — Sort field
- `sortDir` — Sort direction (asc/desc)

---

## 9. Signing Keys & Remote Commands

Test Ed25519 signing key registration, remote command dispatch, and agent-side command receipt.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 9.1 | Generate an Ed25519 key pair locally | Key pair generated | ☐ |
| 9.2 | `POST /api/v1/signing-keys` with public key | Returns 201 with key ID and metadata | ☐ |
| 9.3 | `GET /api/v1/signing-keys` | Returns list of registered signing keys; includes new key | ☐ |
| 9.4 | `POST /api/v1/commands` with signed command payload targeting a machine | Returns 201 with command ID | ☐ |
| 9.5 | Agent calls `Configuration.GetPendingCommands` | Returns the queued command with payload | ☐ |
| 9.6 | Agent calls `Configuration.AcknowledgeCommand` with command ID | Command marked as acknowledged | ☐ |
| 9.7 | `GET /api/v1/commands/{commandId}` | Returns command detail with updated status (acknowledged) | ☐ |
| 9.8 | `GET /api/v1/commands` | Returns list of commands; reflects status of tested command | ☐ |
| 9.9 | `DELETE /api/v1/signing-keys/{keyId}` | Signing key revoked; removed from trusted keys list | ☐ |
| 9.10 | Verify `Configuration.GetConfiguration` no longer returns the revoked key | Revoked key absent from trusted signing keys | ☐ |

**Generating an Ed25519 key pair:**

```bash
# Generate private key
openssl genpkey -algorithm Ed25519 -out signing_key.pem

# Extract public key
openssl pkey -in signing_key.pem -pubout -out signing_key_pub.pem
```

---

## 10. Alerts & Webhooks (Pro/Team Tier)

These features require a Pro or Team tier subscription. Verify tier enforcement before testing.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 10.1 | Verify tenant has Pro or Team subscription | Subscription status shows Pro or Team | ☐ |
| 10.2 | `POST /api/v1/alerts/rules` with alert definition (metric, threshold, condition) | Returns 201 with alert rule details | ☐ |
| 10.3 | `GET /api/v1/alerts/rules` | Returns list of alert rules; includes newly created rule | ☐ |
| 10.4 | `POST /api/v1/alerts/webhooks` with webhook endpoint URL | Returns 201 with webhook details | ☐ |
| 10.5 | `GET /api/v1/alerts/webhooks` | Returns list of webhooks; includes newly created webhook | ☐ |
| 10.6 | Submit telemetry that exceeds the alert threshold (via gRPC `Telemetry.SubmitTelemetry`) | Alert triggered | ☐ |
| 10.7 | `GET /api/v1/alerts/events` | Returns list of alert events; includes triggered alert | ☐ |
| 10.8 | Verify webhook delivery to the configured endpoint | Webhook POST received with alert payload | ☐ |
| 10.9 | `POST /api/v1/alerts/events/{eventId}/acknowledge` | Alert event acknowledged | ☐ |
| 10.10 | `PUT /api/v1/alerts/rules/{ruleId}` with updated threshold | Alert rule updated | ☐ |
| 10.11 | `DELETE /api/v1/alerts/rules/{ruleId}` | Alert rule deleted | ☐ |
| 10.12 | `DELETE /api/v1/alerts/webhooks/{webhookId}` | Webhook deleted | ☐ |

**Tip:** Use a service like [webhook.site](https://webhook.site) or a local listener (`nc -l 9999`) to receive webhook deliveries during testing.

---

## 11. Subscription Limits

Test that tier-based limits and feature gates are enforced correctly from the core application's perspective.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 11.1 | `GET /api/v1/billing/subscription` | Returns current subscription status, tier, and limits | ☐ |
| 11.2 | On Free tier: register machines up to the limit (default: 3) | Registrations succeed | ☐ |
| 11.3 | On Free tier: attempt to register one more machine beyond the limit | Registration rejected with limit-exceeded error | ☐ |
| 11.4 | On Free tier: attempt to create an alert rule (`POST /api/v1/alerts/rules`) | Rejected — alerts are a Pro/Team feature | ☐ |
| 11.5 | On Free tier: attempt to configure custom OIDC (`PUT /api/v1/tenants/{tenantId}/oidc-config`) | Rejected — custom OIDC is a Team feature | ☐ |
| 11.6 | On Pro tier: verify unlimited machine registration | Additional machines register successfully | ☐ |
| 11.7 | On Pro tier: verify alerts are available | Alert rule creation succeeds | ☐ |
| 11.8 | On Team tier: verify custom OIDC configuration is available | OIDC config update succeeds | ☐ |

**Default Free tier limits (configurable via environment):**
- Machine limit: 3 (`Subscription:FreeTierMachineLimit`)
- Telemetry retention: 1 day (`Subscription:FreeTierRetentionDays`)

For full billing/Stripe testing (checkout, payment failures, webhooks), see [e2e-testing-billing.md](e2e-testing-billing.md).

---

## 12. Data Export

Test tenant data export functionality.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 12.1 | `POST /api/v1/tenants/{tenantId}/export` | Returns 202 with export ID | ☐ |
| 12.2 | `GET /api/v1/tenants/{tenantId}/export/status` | Returns export status (pending → processing → completed) | ☐ |
| 12.3 | Poll status until export completes | Status transitions to completed with download info | ☐ |
| 12.4 | Download and verify export contents | Export contains tenant data (machines, telemetry, members, audit log) | ☐ |

---

## 13. Dashboard & Audit

Test dashboard endpoints and verify audit log captures actions from prior test steps.

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 13.1 | `GET /api/v1/dashboard/summary` | Returns dashboard summary (machine count, online/offline, alerts) | ☐ |
| 13.2 | `GET /api/v1/dashboard/fleet` | Returns paginated fleet overview | ☐ |
| 13.3 | `GET /api/v1/audit-log` | Returns audit log entries | ☐ |
| 13.4 | Verify audit log contains entries for actions performed during testing | Entries present for: tenant creation, machine registration, invitation sent, role changes, command dispatch, etc. | ☐ |

---

## 14. Web UI Walkthrough

Manually verify that key pages render correctly and are functional.

| # | Page / Flow | What to Verify | Pass |
|---|-------------|----------------|------|
| 14.1 | `/dashboard` | Fleet overview renders: machine counts, status summary, recent activity | ☐ |
| 14.2 | `/machines` | Machine list with pagination, search bar, OS/status filters, sort controls | ☐ |
| 14.3 | `/machines/{machineId}` | Machine detail page: telemetry charts, status indicators, certificate list | ☐ |
| 14.4 | `/machines/query` | Machine query/search page functions | ☐ |
| 14.5 | `/machines/ssh-sessions` | SSH sessions list renders | ☐ |
| 14.6 | `/settings/members` | Member list, invite button, role dropdown, remove button | ☐ |
| 14.7 | `/settings/tokens` | Registration tokens list, create/revoke actions | ☐ |
| 14.8 | `/settings/signing-keys` | Signing keys list, register/revoke actions | ☐ |
| 14.9 | `/settings/alerts` | Alert rules list, create/edit/delete (Pro/Team only) | ☐ |
| 14.10 | `/settings/billing` | Subscription info, upgrade/downgrade options | ☐ |
| 14.11 | `/settings/audit-log` | Audit log table with filters and pagination | ☐ |
| 14.12 | `/account` | User account page renders | ☐ |
| 14.13 | `/account/settings` | Account settings page with editable fields | ☐ |
| 14.14 | `/admin` (global admin only) | System settings page, user list (only visible to global admins) | ☐ |
| 14.15 | Responsive layout | Resize browser to mobile (375px) and tablet (768px) widths; verify layout adapts without overflow or broken elements | ☐ |

---

## 15. Authorization & Access Control

Test that role-based access control is enforced correctly across all roles.

### Unauthenticated Access

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.1 | `GET /api/v1/machines` without session cookie or API key | Returns 401 Unauthorized | ☐ |
| 15.2 | `GET /api/v1/tenants` without session cookie | Returns 401 Unauthorized | ☐ |

### Cross-Tenant Isolation

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.3 | Sign in as User A (Tenant A), attempt `GET /api/v1/machines/{machineIdFromTenantB}` | Returns 403 Forbidden (or 404) | ☐ |
| 15.4 | Attempt `GET /api/v1/tenants/{tenantBId}` as User A | Returns 403 Forbidden (or 404) | ☐ |

### Viewer (Read-Only) Restrictions

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.5 | As Viewer: `POST /api/v1/tenants/{tenantId}/registration-token` | Returns 403 Forbidden | ☐ |
| 15.6 | As Viewer: `DELETE /api/v1/machines/{machineId}` | Returns 403 Forbidden | ☐ |
| 15.7 | As Viewer: `POST /api/v1/invitations` | Returns 403 Forbidden | ☐ |
| 15.8 | As Viewer: `GET /api/v1/machines` | Returns 200 OK (read access allowed) | ☐ |

### MachineAdmin Restrictions

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.9 | As MachineAdmin: `DELETE /api/v1/machines/{machineId}` | Returns 200 OK (machine management allowed) | ☐ |
| 15.10 | As MachineAdmin: `POST /api/v1/invitations` | Returns 403 Forbidden (tenant settings not allowed) | ☐ |
| 15.11 | As MachineAdmin: `PUT /api/v1/invitations/members/{memberId}/role` | Returns 403 Forbidden | ☐ |

### TenantAdmin Restrictions

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.12 | As TenantAdmin: `POST /api/v1/invitations` | Returns 200 OK (tenant management allowed) | ☐ |
| 15.13 | As TenantAdmin: `GET /api/v1/admin/settings` | Returns 403 Forbidden (system-wide admin required) | ☐ |
| 15.14 | As TenantAdmin: `GET /api/v1/admin/users` | Returns 403 Forbidden | ☐ |

### Global Admin Access

| # | Step | Expected Result | Pass |
|---|------|-----------------|------|
| 15.15 | As Global Admin: `GET /api/v1/admin/settings` | Returns 200 OK with system settings | ☐ |
| 15.16 | As Global Admin: `POST /api/v1/admin/settings` | Returns 200 OK; settings updated | ☐ |
| 15.17 | As Global Admin: `GET /api/v1/admin/users` | Returns 200 OK with user list | ☐ |

---

## 16. Teardown

### Docker Compose

```bash
cd deployment/server/docker/
docker compose down

# To also remove volumes (persistent data):
docker compose down -v
```

### Local Build

1. Stop the web dev server (`Ctrl+C` in the `pnpm dev` terminal)
2. Stop the API server (`Ctrl+C`)
3. Optionally stop infrastructure:

   ```bash
   cd deployment/server/docker/
   docker compose down postgres redis
   ```

4. If using a persistent database, clean up test data manually or drop the database:

   ```bash
   psql -U vordfleet -c "DROP DATABASE vordfleet;"
   ```
