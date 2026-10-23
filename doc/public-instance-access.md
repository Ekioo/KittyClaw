# Managed public instance access

## Purpose and current delivery

Each opted-in installation gets a stable address such as
`https://kc-<installation-id>.kittyclaw.dev`. The desktop connector and local
activation flow are implemented. The separate invitation-only provisioning Worker
lives in the KittyClaw-Front repository under `remote-access-worker/`.

The Worker is not deployed. Desktop tests replace the broker and connector with
isolated fakes. No production tunnel, DNS record or public address has been created.
A real Cloudflare/Android acceptance run remains required before beta publication.

## Owner activation

Global settings links to `/remote-setup`, a server-rendered local-only page. Its
mutations require antiforgery validation. Remote callers cannot activate, pause or
read installation credentials through this page. The owner first associates the
[network authenticator](./network-access.md), then enters a beta invitation and
accepts Cloudflare transit. No Cloudflare account or API key is requested from the
end user.

`RemoteAccessStore` persists a random installation ID and credential, protected with
the existing network Data Protection provider, before making an allocation request.
A failed or lost registration response can be retried with the same identity. The
broker origin and dedicated port are bound to this identity; an accidental service
change cannot forward the saved credential elsewhere. Registration validates the
returned instance ID, exact HTTPS hostname and port.

The first successful activation reserves the address and requests one restart to
bind the dedicated origin. Later stop/resume operations retain the URL and require
no restart while that origin is present. Stopping first closes the HTTP origin gate,
then cancels the connector and revokes browser sessions. Session revocation preserves
the authenticator but also signs out existing LAN sessions. Restarting a paused
installation keeps the dedicated origin bound and closed so resume is immediate.
Disabling the authenticator cancels the connector and prevents startup or resume.

The setup page displays the public URL and its QR code, state, error explanation,
and a refresh link. It never renders the installation credential or tunnel token.

## Components and configuration

- `RemoteAccessStore`: encrypted `remote-access.json` ownership and opt-in state.
- `RemoteAccessBroker`: bounded HTTPS requests, per-installation bearer identity,
  no redirects, fixed response validation and safe user-facing error codes.
- `RemoteAccessConnector`: hosted supervisor, activation, pause, restart detection,
  reconnect backoff and permanent-error intervention.
- `CloudflaredRuntime`: pinned binary download/integrity checks, hidden child
  process, readiness polling, stream draining and shutdown.
- `RemoteAccessSetupMiddleware`: local antiforgery-protected setup forms.
- `RemoteTunnelMiddleware`: dedicated origin boundary and browser API exceptions.
- Named `KittyClawLocal` HTTP client: existing server-side UI API calls use the
  trusted local listener rather than the browser's public origin. Caller cookies
  and installation credentials are never forwarded on these self-calls.

The broker defaults to `https://remote-api.kittyclaw.dev` and can be overridden
locally with `RemoteAccess:BrokerUrl` (a clean HTTPS origin). `RemoteTunnel:Port`
selects the initial dedicated port, default 5234. It must differ from the primary
and LAN listeners. Once registered, the saved port and assigned hostname define
startup configuration. The legacy `RemoteTunnel:Enabled`/`Hostname` settings remain
available for manually configured origin tests.

As elsewhere in KittyClaw, `KITTYCLAW_DATA_DIR` isolates the registry and key files.
Preserve `remote-access.json` and `network-keys/` together. Corrupt protected state
fails startup rather than discarding installation ownership. Copying a data directory
copies the identity; independent clones need a future ownership recovery flow.

## Connector lifecycle and integrity

Managed assets are pinned to cloudflared **2026.10.3** with their upstream SHA-256
digests compiled into `CloudflaredRuntime`. Windows x64, Linux x64/arm64 and macOS
x64/arm64 are supported. Other platforms report an explicit unsupported state.
The connector is fetched from the versioned Cloudflare GitHub release, bounded to
128 MiB and verified before execution. Cached assets are verified again before
being installed. macOS extraction accepts only the regular `cloudflared` entry.
Unix executables have owner read/execute permissions.

`TUNNEL_TOKEN` carries the in-memory connector credential into the child
environment. It is never included in command arguments, plaintext token files,
application logs or UI. Upstream stdout/stderr are drained without logging.
Auto-update is disabled so an upstream restart cannot bypass the pinned version.

The metrics endpoint binds an ephemeral loopback port. `/ready` must return 200
before the UI says connected. This confirms connector readiness, not a successful
remote browser login. Readiness loss clears the online state. Unexpected exit or
transient broker/network failure reconnects with bounded exponential backoff and
jitter. Retired identities, ownership failures and unsupported platforms await
owner intervention. Shutdown and pause cancel the attempt and kill the child tree.

## Origin and browser protection

The origin listener is loopback-only, with an exact assigned public Host. Its port
marks requests as remote before the existing network authentication middleware, so
localhost transport cannot grant local privileges. It represents an explicitly
configured HTTPS edge; forwarding headers do not establish trust. The Cloudflare
ingress must preserve that Host and target this port, never the trusted agent port.

Every allowed request still passes authenticator/session protection. API, MCP and
OpenAPI paths remain denied except these browser requirements:

- `GET /api/engine/health` for reconnect checks;
- `GET /api/projects/<slug>/runs/<run-id>/stream` for authenticated SSE;
- `POST /api/images` with the exact public Origin for browser uploads.

Image requests with absent or cross-site Origin are denied even with a cookie.
The existing gate also validates Origin on Blazor handshakes and other requests.
Login pages use same-origin referrer policy so browser POSTs carry a usable Origin.
The local setup path is denied to remote callers. Pausing denies new origin requests
and revokes active remote transports even if a connector continues forwarding.

Cloudflare terminates public TLS and processes application traffic. This is not
end-to-end encryption past Cloudflare; the consent text describes Cloudflare transit.

## Provisioning service and release gates

The front repository's Worker README owns the broker API, D1 schema, invitation
administration, quotas and resource lifecycle. Global Cloudflare credentials remain
in Worker secrets; each desktop receives only its own tunnel credential. Registration
is invitation-only. Durable state and exact resource lookups handle partial failures.
Permanent retirement differs from desktop pause and retains a name tombstone.

Before release, deploy the reviewed Worker/D1 configuration, add edge rate limits,
inspect real quotas, verify remotely managed token rotation and forced connector
termination, test live DNS/HTTPS readiness, and run Android/Blazor reconnect, SSE and
upload journeys. Finish recovery for lost or cloned installation identities and
establish fleet suspension, monitoring, backup and retention procedures.

## Verification and authoritative references

`RemoteAccessConnectorTests`, `RemoteTunnelTests` and `NetworkAccessTests` cover
ownership persistence, validation, supervision, readiness, pause/resume, origin
isolation, browser API exceptions, CSRF and session invalidation.
`RemoteAccessJourneyTests` runs Edge/Chromium against real Kestrel on an ephemeral
isolated port, with a fake broker and all hosted services removed. It enrolls the
authenticator through the UI, activates, pauses and resumes the same URL, and checks
desktop/mobile layouts. Enable it with `KITTYCLAW_REMOTE_DEBUG_E2E=1`; optionally set
`KITTYCLAW_TEST_BROWSER_CHANNEL`. Screenshots stay under ignored `.artifacts/`.

- [Pinned official release and asset digests](https://api.github.com/repos/cloudflare/cloudflared/releases/tags/2026.10.3).
- [cloudflared arguments and environment variables](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/run-parameters/).
- [Pinned readiness implementation](https://raw.githubusercontent.com/cloudflare/cloudflared/2026.10.3/metrics/readiness.go).
- [cloudflared source and license](https://github.com/cloudflare/cloudflared).
- [Tunnels for Platforms](https://developers.cloudflare.com/tunnel/features/tunnels-for-platforms/).
- [DNS record quotas](https://developers.cloudflare.com/dns/manage-dns-records/).
- [Universal SSL coverage](https://developers.cloudflare.com/ssl/edge-certificates/universal-ssl/limitations/).
- [WebSockets and edge reconnect behavior](https://developers.cloudflare.com/network/websockets/).

The service's included capacities do not imply unlimited free hosting. Confirm the
current Cloudflare account quotas and operating budget before increasing beta size.
