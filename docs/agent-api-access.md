# Agent API access

## Purpose

A headless agent requests an API grant through an enabled managed tunnel. Its owner
approves on their own browser at the fixed URL `https://my.kittyclaw.dev/agents`,
using the existing account sign-in and MFA. The owner never copies a credential to
the agent and no remote desktop, callback to the agent, or browser on its VM is needed.

The verification URL has no query or fragment. The agent displays the confirmation
code separately. That code identifies a pending request; it cannot authorize access
or redeem a token. The independent 256-bit device code stays private on the agent's
machine and is sent only in HTTPS request bodies. Do not put either code or a token
in a URL, log, ticket or chat. Only the confirmation code is shown to the owner.

## Client contract

All requests below use the installation's assigned HTTPS tunnel origin (for example
`https://ekioo.kittyclaw.dev`). No browser cookies, Origin or Fetch Metadata headers
are accepted. Redirects must not be followed when sending device codes or tokens.

1. `POST /api/agent-access/start`, `Content-Type: application/json`:

   ```json
   { "name": "My VM agent", "projects": ["my-project"], "write": true }
   ```

   Project slugs must already exist and be known to the requesting agent. At most
   50 distinct projects can be requested; no wildcard or future-project permission
   is granted. Only the requested slugs are sent to the first-party portal, never
   tickets or project contents. The owner can select a subset and reduce rights.

   Response: `verification_uri` (always the fixed portal `/agents` URL), `user_code`,
   `device_code`, `expires_in: 600`, `interval: 5`. Display only `verification_uri`
   and `user_code` to the owner. Keep `device_code` private in process memory.

2. Poll `POST /api/agent-access/token` with JSON `{ "deviceCode": "<private value>" }`
   no more frequently than the returned interval. HTTP 400 JSON errors:
   `authorization_pending` means wait; `slow_down` means increase the interval by
   five seconds; `access_denied` and `expired_token` end the attempt. Stop after the
   original request expires. HTTP 429 uses `Retry-After`; 503 is a transient
   first-party service failure. Never automatically start another approval request
   after denial or expiry. A lost successful token response requires new approval.

3. After approval, the JSON body contains `access_token`, `token_type: Bearer`,
   `expires_in`, `projects` and `scope`. The token is delivered once. Keep it private
   and use `Authorization: Bearer <access_token>` for subsequent API calls. No token
   is delivered through a browser, link or redirect. There is no refresh token;
   repeat owner approval after expiry.

4. The owner can revoke an issued token under **Agent access** on the portal.
   Revocation applies to the next API request; completed writes are not undone.
   There is no positive validation cache or agent streaming access. Already
   executing requests can finish. Portal outage fails closed with HTTP 503.

## Permissions

Every grant is bound to one account installation and the selected project slugs.
The owner chooses 1 hour (default), 8 hours, 1 day or 7 days, and these rights:

| Scope | Allowed operations |
| --- | --- |
| `project:read` | GET selected project details, tickets and individual ticket activity/evidence/brief, attachments (list/download), columns, labels and members. GET `/api/projects` returns only granted slugs and names. API docs and OpenAPI schema are readable. |
| `tickets:write` | Create/update/delete tickets, comments, acceptance criteria, dependencies, parenting, status, schedule, order and focus within selected projects, and upload/remove ticket attachments. |

Ticket writes can trigger configured workflows and agent dispatches. The approval
page explains this. Cross-project transfers, project creation/configuration/deletion,
secrets, filesystem browsing, explicit provider execution, MCP and streaming remain
denied. Unlisted routes fail closed, including newly added endpoints. The existing
API `author` field remains required; it does not establish authentication.

Anonymous ordinary API requests remain blocked. Invalid or revoked agent tokens
return 401; valid tokens outside their permissions return 403. Existing browser
session rules and the tunnel's limited browser API exceptions are preserved.

## Components and storage

- `RemoteTunnelMiddleware` establishes the loopback/HTTPS/exact-host boundary
  before delegating to `AgentApiAccess`. Paused access closes this surface too.
- `AgentApiAccessService` bounds anonymous start/poll traffic and calls the portal
  through `AccountAssociationService`, using the existing protected installation
  credential. Only authenticated, permitted API requests receive `AgentApiGrant`.
- `AgentApiPolicy` is an explicit method/path capability allowlist; normal browser
  cookies cannot be used as agent credentials or combined with them.
- The portal's `account-worker/src/agents.mjs` owns request approval, atomic token
  issuance, owner listings and revocation. Its migration and deployment contract
  live in the account Worker README in KittyClaw-Front.

Only SHA-256 hashes of device codes and API tokens persist in the portal database.
MFA loss/change and installation/account revocation invalidate agent access.
Disabling MFA and revoking association delete grants so re-enabling cannot restore
them. Agent grants otherwise expire independently of browser logout, permitting
headless work until the chosen expiry or explicit revocation.

## Verification and rollout

`AccountPortalAccessTests` exercises the real middleware order using isolated
TestServer listeners, synthetic accounts and a fake portal. It checks permissions,
project filtering, browser rejection, response bounds, central outages and immediate
revocation, without a live tunnel or agent dispatch. Portal tests exercise actual
SQLite migrations, ownership/MFA/CSRF, denial, narrowing rights, replay/concurrent
redemption, rate limits, expiry and revoked/foreign installation tokens.

Rollout requires applying account migration `0004_agent_access.sql`, deploying the
account Worker, and installing/restarting the new KittyClaw build. A source change
alone does not update an already installed desktop binary. No new service secret
or Cloudflare permission is required.
