# Local network access

KittyClaw remains loopback-only by default. Global settings links to
`/network-setup`, a server-rendered setup page accessible only through a loopback
connection with a loopback Host header. Association and reset require antiforgery
validation. Scan the locally generated QR code or add the displayed setup key to a
TOTP authenticator (SHA-1, six digits,
30 seconds), then confirm a code. No password or external identity service is used;
this is authenticator-only authentication, not two independent factors.

Settings opens the standalone setup documents with full navigation. Local setup
pages load the shared Windows desktop toolbar styles and allow same-origin images
so the native shell keeps its logo and window controls. Remote login pages retain
their self-contained asset policy.

## Listener and host validation

After association, restart KittyClaw. The original listener remains available for
local UI, agent APIs and MCP. An additional HTTPS listener binds all interfaces on
port 5233 (`NetworkAccess:HttpsPort` overrides it; use a distinct port for isolated
instances). The setup page lists current interface URLs. Only explicit loopback
hosts, the machine name and interface IP addresses pass host filtering; the default
`appsettings.json` allowlist stays restricted. Restart after an interface/address
change to update this list.

The HTTPS certificate is self-signed, generated on this PC and reused across
restarts. Verify its SHA-256 fingerprint on the setup page before trusting it on
another device. Allow the HTTPS port through the OS firewall for the private
network if needed; KittyClaw does not modify firewall rules. This feature supports
direct LAN connections. The dedicated origin for managed public access is documented in [public instance access](./public-instance-access.md). Do not proxy
remote requests through localhost: loopback access is intentionally trusted for
local automation.

## Request protection

The local-only `/network-setup` page also offers an optional direct-network IP
allowlist. Enable filtering and enter individual IPv4/IPv6 addresses or CIDR
networks, one per line (maximum 256). Filtering is disabled by default for existing
installations. An enabled empty list denies every direct remote client. Invalid
entries reject the entire update. Rules are protected in the existing network state
and survive authenticator enrollment, reset and server restart.

Rules use the actual socket peer, normalizing IPv4-mapped IPv6 addresses; forwarded
headers cannot grant access. They apply before login to direct remote requests for
UI, APIs, uploads, MCP and Blazor. HTTPS and authenticator sessions remain required.
Changes apply immediately and abort active authenticated connections whose peer is
no longer allowed. Localhost remains available for recovery. The managed Cloudflare
tunnel is excluded from this allowlist and retains its own session authentication.
Use stable device addresses or DHCP reservations when allowing individual devices.

`NetworkAccessMiddleware` runs before application endpoints and uploaded files.
Global response headers block framing, embedded objects and MIME sniffing, and
limit referrer disclosure to the same origin. Endpoint-specific content security
policies remain in force. The generated API documentation fetches its OpenAPI
specification through the configured trusted local client, with redirects and
proxies disabled; incoming Host and scheme values never select that destination.
All non-loopback requests require enabled access, HTTPS and a valid session,
including REST APIs, MCP and Blazor/SignalR handshakes. Only the login form is
anonymous. APIs receive 401 rather than HTML redirects. Cross-origin browser requests are rejected before local trust is applied, including
forms and WebSocket handshakes targeting localhost. Browser fetch metadata rejects
cross-site writes with no Origin; ordinary headerless local agent clients and
user-initiated local navigation remain supported. Remote HTTP requests are rejected, never used for login.

Login uses RFC 6238 TOTP, accepting a one-step clock drift. Accepted time steps are
persisted to prevent reuse across restarts. Five failed attempts cause a one-minute cooldown for that source address. Sources
are hashed, kept in a bounded in-memory registry and expire after inactivity. Direct
LAN requests use the actual socket peer; only the dedicated tunnel boundary uses
Cloudflare's overwritten `CF-Connecting-IP` header. `X-Forwarded-For` is never used.
A failed login from one source does not block a valid login from another source. Authenticated sessions expire after 12 hours and use a
protected `__Host-` cookie with Secure, HttpOnly and SameSite=Strict. There are no
anonymous exceptions for agent APIs or MCP; local integrations continue to use
localhost.

Logout persists a SHA-256 revocation key for the current protected cookie until
its expiration and immediately aborts that session's active remote transports.
Other sessions and the connector remain online. Existing protected cookies keep
their format, so upgrading does not require re-enrollment. Expired revocation
entries are pruned when authenticating or revoking a session.

Disabling access from localhost immediately revokes sessions and closes active
remote transports, including Blazor WebSockets. It also removes the authenticator
association. Re-enroll locally if the phone is lost. Restart to remove the HTTPS
listener itself. The local instance is never restarted automatically during setup.

## Storage and verification

`NetworkAccessService` stores protected state in `<dataDir>/network-access.json`,
the protected certificate in `network-access.json.certificate`, and Data Protection
keys under `network-keys/`. Web antiforgery and Blazor protection keys are isolated
under `web-keys/` in the same data directory. Windows protects the keys with current-user DPAPI;
other platforms rely on data-directory permissions. Preserve these files together
when moving the installation. Unreadable protected state fails startup rather than
silently dropping authentication.

`NetworkAccessTests` verifies official RFC vectors, enrollment, replay protection,
throttling, persistence, expiry, reset, secure cookies, CSRF, origin validation and
middleware protection of APIs, uploads and MCP using an isolated TestServer. A
full-host test also verifies saved configuration, real settings rendering and
host filtering with all hosted services removed. Neither test dispatches agents.
