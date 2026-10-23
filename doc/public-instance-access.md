# Managed public instance access

## Purpose and delivery status

Give each opted-in KittyClaw installation a stable HTTPS address such as
`https://kc-<random-instance-id>.kittyclaw.dev`, without router configuration or a
Cloudflare account on the user's device. The desktop implementation establishes
the origin trust boundary. A separate invitation-only provisioning Worker now lives
in the KittyClaw-Front repository under remote-access-worker/; it includes the D1
registry, ownership checks, quotas, tunnel/DNS reconciliation and permanent
retirement. It is tested locally and not deployed. No cloudflared supervisor,
activation UI, public DNS changes or Internet deployment is included yet.

## Implemented origin boundary

`RemoteTunnelOptions` and `RemoteTunnelMiddleware` in
`KittyClaw.Web/Services/RemoteTunnelMiddleware.cs` define a disabled-by-default,
loopback-only HTTP origin. Startup adds it independently of the existing local and
LAN listeners. Example development configuration (not a public deployment):

```json
{
  "RemoteTunnel": {
    "Enabled": true,
    "Port": 5234,
    "Hostname": "kc-abcdef123456.kittyclaw.dev"
  }
}
```

The port must differ from all existing listeners. Startup validates the assigned
hostname and adds exactly that host to host filtering. The dedicated listener
requires a loopback peer, the exact public host and the default public HTTPS port.
It marks the request before the existing authentication middleware, so localhost
transport cannot grant local privileges. It represents an HTTPS edge explicitly;
client-supplied forwarding headers do not establish trust. The connector must
preserve the assigned public Host and target this port, never the trusted local
UI/agent port.

The existing enrollment, TOTP, session and revocation logic remains owned by
[local network access](./network-access.md). Enrollment is required before the
origin can serve a remote login. The setup page stays local-only. Public origin
responses disable caching and indexing. API, MCP and OpenAPI paths are currently
closed on this origin, including authenticated sessions. Browser-dependent API
routes must be audited and selectively authorized before the browser UI is ready
for a public beta. In particular, image upload, SSE and reconnect health checks
need explicit decisions; blocking all APIs is an interim security boundary, not a
finished remote UI.

## Target control plane

Use a Cloudflare Worker with D1 for registration and lifecycle metadata. Use one
remotely managed Cloudflare Tunnel and one proxied DNS record per instance. Browser
traffic flows through Cloudflare Tunnel, not through the provisioning Worker.
The site at kittyclaw.dev remains separate. First-level `kc-*` names avoid requiring
additional certificate coverage for nested subdomains.

An authenticated installation identity owns its hostname and tunnel. The global
Cloudflare management credential stays in Worker secrets. Return only that
installation's tunnel credential, protect it at rest and never log it or put it in
process arguments. Design bootstrap, ownership verification and abuse controls
before accepting anonymous registration; there must be no public endpoint that
allocates unlimited tunnels or reissues credentials based only on a hostname.

Provisioning must use durable operation state and idempotency, with reconciliation
for partial Cloudflare/DNS/database failures. Concurrent requests must not allocate
duplicate names. Treat pending, active, suspended, retiring and deleted states
explicitly. Disable must stop forwarding and disconnect active sessions; remote
revocation and tunnel-token rotation must terminate existing connector sessions,
not merely issue a replacement token. Retired hostnames must not be reassigned.
Account for restored/cloned data directories and installation ownership recovery.

## Remaining milestones

1. Validate the origin against real Kestrel and an isolated cloudflared tunnel;
   exercise login, cookies, CSRF, origin checks and Blazor reconnection on Android.
2. Audit browser API usage and define an authenticated remote allowlist. Prove
   that automation and administrative APIs remain inaccessible. Assess the shared
   registrable domain's same-site behavior; keep cookies host-only and validate
   Origin for all relevant requests and WebSocket handshakes.
3. Validate and deploy the Worker/D1 control plane from KittyClaw-Front, including
   quotas, tunnel/DNS provisioning and permanent retirement. Expand the existing fake
   Cloudflare API tests for retries, races, authorization and partial failures.
4. Add a supervised connector client with a verified, pinned cloudflared binary,
   protected token delivery, cancellation, bounded backoff and clean shutdown.
   Verify supported platforms and redistribution licensing.
5. Add local settings UI: opt-in, authentication enrollment, stable URL/QR,
   connecting/online/offline/error states, disable and recovery. Never display an
   online state based only on a running process.
6. Run a limited invitation beta and measure connections, traffic, cost and
   support load before general availability. Add operational alerts, backup and
   recovery, retention, abuse handling and a fleet-wide emergency suspension path.

## Verification

`RemoteTunnelTests` covers default-off configuration, invalid hostnames, listener
separation, spoofed forwarding headers, disabled authentication, protected uploads
and Blazor handshakes, denied agent surfaces and local-only enrollment. Existing
`NetworkAccessTests` cover authentication and LAN behavior. Use isolated mock-backed
instances for end-to-end work; never test against the user's main instance.

## External dependencies and capacity

Cloudflare terminates public TLS and can process application traffic. Explain this
in opt-in information. Do not describe the connection as end-to-end encrypted past
Cloudflare. Revalidate these published limits before rollout and inspect the actual
account/zone quotas:

- [Tunnels for Platforms](https://developers.cloudflare.com/tunnel/features/tunnels-for-platforms/):
  1,000 included tunnels; additional capacity is currently $4.50/tunnel/month.
  Inactive tunnels consume capacity too.
- [DNS record quotas](https://developers.cloudflare.com/dns/manage-dns-records/):
  recent Free zones generally have 200 records and Pro zones 3,500. Existing site
  records consume part of the quota. Check this before selecting a beta limit.
- [Universal SSL coverage](https://developers.cloudflare.com/ssl/edge-certificates/universal-ssl/limitations/):
  ordinary full-zone coverage includes first-level subdomains.
- [WebSockets](https://developers.cloudflare.com/network/websockets/):
  edge restarts can interrupt connections; reconnect/keepalive must be tested.
- [Tunnel provisioning API](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel-api/):
  management permissions and required outbound connectivity.

Workers/D1, rate limiting, traffic restrictions and service terms require a separate
operating-budget review. Included tunnel capacity does not imply an unlimited free
hosted service. No cloud resources should be created from tests.
